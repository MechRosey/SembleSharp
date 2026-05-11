using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Semble.Encoders;

/// <summary>
/// Minimal reader for HuggingFace `tokenizer.json` files. Supports the
/// BERT-style WordPiece pipeline used by every model2vec / potion model
/// I'm aware of (and the mainline BERT lineage). Other tokenizer types
/// (BPE, Unigram, ByteLevel) throw <see cref="NotSupportedException"/>
/// with a clear message — extending support is per-model and additive.
/// </summary>
/// <remarks>
/// Encoding pipeline:
///   1. <see cref="BertNormalizer"/> — optional lowercase + accent-strip via NFD
///   2. <see cref="BertPreTokenizer"/> — whitespace split, then isolate punctuation
///   3. WordPiece — longest-prefix vocab lookup with `##` continuation prefix,
///      emit the unk token if no prefix matches, and emit unk for words
///      longer than the configured max-chars-per-word.
/// `add_special_tokens=False` semantics: post-processor (CLS/SEP) is skipped.
/// </remarks>
public sealed class HuggingFaceTokenizer
{
    private readonly IReadOnlyDictionary<string, int> _vocab;
    private readonly int _unkTokenId;
    private readonly string _unkToken;
    private readonly string _continuingPrefix;
    private readonly int _maxInputCharsPerWord;
    private readonly bool _lowercase;
    private readonly bool _stripAccents;

    public int VocabSize => _vocab.Count;
    public int UnkTokenId => _unkTokenId;

    private HuggingFaceTokenizer(
        IReadOnlyDictionary<string, int> vocab,
        string unkToken,
        int unkTokenId,
        string continuingPrefix,
        int maxInputCharsPerWord,
        bool lowercase,
        bool stripAccents)
    {
        _vocab = vocab;
        _unkToken = unkToken;
        _unkTokenId = unkTokenId;
        _continuingPrefix = continuingPrefix;
        _maxInputCharsPerWord = maxInputCharsPerWord;
        _lowercase = lowercase;
        _stripAccents = stripAccents;
    }

    public static HuggingFaceTokenizer LoadFromJson(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadFromJson(stream, sourceForErrors: path);
    }

    public static HuggingFaceTokenizer LoadFromJson(Stream stream, string sourceForErrors = "<stream>")
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        // Normalizer config
        bool lowercase = false;
        bool stripAccents = false;
        if (root.TryGetProperty("normalizer", out var norm) && norm.ValueKind != JsonValueKind.Null)
        {
            (lowercase, stripAccents) = ParseNormalizer(norm, sourceForErrors);
        }

        // Pre-tokenizer config — only `BertPreTokenizer` / `Whitespace` /
        // `WhitespaceSplit` (and Sequence chains of those) are supported.
        if (root.TryGetProperty("pre_tokenizer", out var pre) && pre.ValueKind != JsonValueKind.Null)
        {
            ValidatePreTokenizerSupported(pre, sourceForErrors);
        }

        if (!root.TryGetProperty("model", out var model))
            throw new InvalidDataException($"tokenizer.json missing required 'model' object: {sourceForErrors}");

        var modelType = model.GetProperty("type").GetString();
        if (!string.Equals(modelType, "WordPiece", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"tokenizer.json model.type='{modelType}' not supported by Semble's built-in " +
                $"tokenizer (only WordPiece is). Source: {sourceForErrors}. Open an issue or " +
                $"plug in a custom IEncoder if you need this model.");
        }

        var unkToken = model.TryGetProperty("unk_token", out var unkProp) ? unkProp.GetString() ?? "[UNK]" : "[UNK]";
        var continuingPrefix = model.TryGetProperty("continuing_subword_prefix", out var cspProp)
            ? cspProp.GetString() ?? "##"
            : "##";
        int maxChars = model.TryGetProperty("max_input_chars_per_word", out var mcProp)
            ? mcProp.GetInt32()
            : 100;

        var vocabProp = model.GetProperty("vocab");
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in vocabProp.EnumerateObject())
        {
            vocab[entry.Name] = entry.Value.GetInt32();
        }

        if (!vocab.TryGetValue(unkToken, out int unkId))
        {
            throw new InvalidDataException(
                $"tokenizer.json declares unk_token='{unkToken}' but it is not present in vocab. " +
                $"Source: {sourceForErrors}");
        }

        return new HuggingFaceTokenizer(vocab, unkToken, unkId, continuingPrefix, maxChars, lowercase, stripAccents);
    }

    private static (bool Lowercase, bool StripAccents) ParseNormalizer(JsonElement norm, string source)
    {
        // Top-level normalizer can be a single object or { type: "Sequence", normalizers: [...] }.
        if (norm.TryGetProperty("type", out var typeProp))
        {
            var t = typeProp.GetString();
            if (string.Equals(t, "Sequence", StringComparison.Ordinal))
            {
                bool lc = false, sa = false;
                foreach (var n in norm.GetProperty("normalizers").EnumerateArray())
                {
                    var (l, s) = ParseNormalizer(n, source);
                    lc |= l;
                    sa |= s;
                }
                return (lc, sa);
            }
            if (string.Equals(t, "BertNormalizer", StringComparison.Ordinal))
            {
                bool lc = norm.TryGetProperty("lowercase", out var lp) && lp.ValueKind == JsonValueKind.True;
                bool sa = norm.TryGetProperty("strip_accents", out var sp)
                    && sp.ValueKind == JsonValueKind.True;
                // BertNormalizer's `strip_accents=null` defaults to `lowercase` per HF source.
                if (norm.TryGetProperty("strip_accents", out var sp2) && sp2.ValueKind == JsonValueKind.Null)
                    sa = lc;
                return (lc, sa);
            }
            if (string.Equals(t, "Lowercase", StringComparison.Ordinal))
                return (true, false);
            if (string.Equals(t, "StripAccents", StringComparison.Ordinal) ||
                string.Equals(t, "NFD", StringComparison.Ordinal))
                return (false, true);
            if (string.Equals(t, "NFC", StringComparison.Ordinal) ||
                string.Equals(t, "NFKD", StringComparison.Ordinal) ||
                string.Equals(t, "NFKC", StringComparison.Ordinal) ||
                string.Equals(t, "Replace", StringComparison.Ordinal) ||
                string.Equals(t, "Strip", StringComparison.Ordinal) ||
                string.Equals(t, "Prepend", StringComparison.Ordinal))
            {
                // No-op for our purposes — no characters added/removed at the
                // ASCII level we care about for code text.
                return (false, false);
            }
            throw new NotSupportedException(
                $"tokenizer.json normalizer.type='{t}' not supported. Source: {source}");
        }
        return (false, false);
    }

    private static void ValidatePreTokenizerSupported(JsonElement pre, string source)
    {
        if (!pre.TryGetProperty("type", out var typeProp))
            return;
        var t = typeProp.GetString();
        if (t is "BertPreTokenizer" or "Whitespace" or "WhitespaceSplit" or "Punctuation" or "Metaspace")
            return;
        if (t is "Sequence")
        {
            foreach (var inner in pre.GetProperty("pretokenizers").EnumerateArray())
                ValidatePreTokenizerSupported(inner, source);
            return;
        }
        throw new NotSupportedException(
            $"tokenizer.json pre_tokenizer.type='{t}' not supported by Semble's built-in tokenizer. Source: {source}");
    }

    /// <summary>
    /// Encode <paramref name="text"/> into token IDs, mirroring HF
    /// <c>tokenizer.encode(text, add_special_tokens=False).ids</c>. Truncated
    /// to <paramref name="maxLength"/> tokens.
    /// </summary>
    public List<int> Encode(string text, int maxLength = 512)
    {
        var normalized = Normalize(text);
        var preTokens = PreTokenize(normalized);
        var ids = new List<int>();
        foreach (var word in preTokens)
        {
            if (ids.Count >= maxLength)
                break;
            WordPieceEncode(word, ids, maxLength);
        }
        return ids;
    }

    private string Normalize(string text)
    {
        if (!_lowercase && !_stripAccents)
            return text;

        string s = text;
        if (_stripAccents)
        {
            s = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            s = sb.ToString();
        }
        if (_lowercase)
            s = s.ToLowerInvariant();
        return s;
    }

    /// <summary>
    /// BertPreTokenizer-equivalent: split on Unicode whitespace, then split
    /// off punctuation runs as their own tokens.
    /// </summary>
    private static List<string> PreTokenize(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (IsControlOrInvisible(c))
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else if (IsBertPunctuation(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                result.Add(c.ToString());
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
            result.Add(sb.ToString());
        return result;
    }

    private static bool IsControlOrInvisible(char c)
    {
        if (c == '\t' || c == '\n' || c == '\r')
            return false;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    private static bool IsBertPunctuation(char c)
    {
        // BERT treats all ASCII non-alphanumeric printables as punctuation
        // PLUS Unicode punctuation categories.
        if (c <= 0x7e)
        {
            if ((c >= 33 && c <= 47) ||
                (c >= 58 && c <= 64) ||
                (c >= 91 && c <= 96) ||
                (c >= 123 && c <= 126))
                return true;
            return false;
        }
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private void WordPieceEncode(string word, List<int> output, int maxLength)
    {
        if (word.Length > _maxInputCharsPerWord)
        {
            if (output.Count < maxLength)
                output.Add(_unkTokenId);
            return;
        }

        // Greedy longest-prefix match. If any sub-piece is unmatched, the whole
        // word becomes a single unk (matching HF reference behaviour).
        var subTokens = new List<int>();
        int start = 0;
        while (start < word.Length)
        {
            int end = word.Length;
            int matchedId = -1;
            while (end > start)
            {
                var piece = start == 0
                    ? word[start..end]
                    : _continuingPrefix + word[start..end];
                if (_vocab.TryGetValue(piece, out var id))
                {
                    matchedId = id;
                    break;
                }
                end--;
            }
            if (matchedId < 0)
            {
                if (output.Count < maxLength)
                    output.Add(_unkTokenId);
                return;
            }
            subTokens.Add(matchedId);
            start = end;
        }

        foreach (var id in subTokens)
        {
            if (output.Count >= maxLength)
                return;
            output.Add(id);
        }
    }
}
