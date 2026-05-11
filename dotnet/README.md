# Semble (.NET port)

Pure .NET 8 port of [Semble](https://github.com/MinishLab/semble) -- a fast,
accurate, token-efficient code-search library for agents. The Python source has
been removed; this directory is the canonical implementation.

## Layout

```
dotnet/
+-- Semble.sln
+-- Directory.Build.props          # nullable, latest LangVersion, warnings-as-errors
+-- src/
|   +-- Semble/                    # library: types, tokens, ranking, index, search, formatting
|   +-- Semble.Cli/                # search / find-related / init subcommands
|   +-- Semble.Mcp/                # MCP stdio server (ModelContextProtocol 1.2.0)
+-- tests/
    +-- Semble.Tests/              # 240 cases
    +-- Semble.Cli.Tests/          # 18 cases
    +-- Semble.Mcp.Tests/          # 40 cases
```

## Build / test

```bash
dotnet build dotnet/Semble.sln
dotnet test dotnet/Semble.sln
```

A C compiler is required to build the vendored tree-sitter natives:

| Platform | Compiler | Notes |
|---|---|---|
| Linux   | `cc` / `gcc` / `clang` | preinstalled on most distros and on `ubuntu-latest` runners |
| macOS   | `clang`                 | ships with Xcode Command Line Tools |
| Windows | `cl.exe`                | run from a Visual Studio Developer Command Prompt; CI uses `ilammy/msvc-dev-cmd@v1` |

The build script picks `cc` if present, otherwise falls back to `clang` then
`gcc`. Override with `CC=...`.

CI (`.github/workflows/dotnet.yaml`) runs the same on every push to `main` and
the migration branch, on Ubuntu / macOS / Windows.

## Migration status

| Chunk | Module | Status |
|---|---|---|
| 0  | Solution scaffold + CI                              | done |
| 1  | Core types (`Chunk`, `SearchResult`, `IEncoder`)    | done |
| 2  | Tokenisation (`tokens.py`)                          | done -- byte-for-byte parity vs Python |
| 3  | Ranking (`weighting`, `boosting`, `penalties`)      | done -- exact-value parity (2.75 / 1.625 / 2.75) |
| 4  | File walker + `.gitignore`                          | done -- minimal pathspec subset, validated against pathspec |
| 5  | Code-aware chunker                                  | done -- line-based default + Roslyn for C# + tree-sitter for C++. tree-sitter source is **vendored** under `dotnet/native/` and built from source by `build-natives.{sh,cmd}`; no third-party tree-sitter NuGets are referenced. Other languages still fall back to line-based |
| 6  | BM25 + path enrichment                              | done -- bit-for-bit parity with `bm25s.BM25(method='lucene')` |
| 7  | Dense backend                                       | done -- brute-force cosine + stable top-k |
| 8  | Index orchestration (`FromPath` / `FromGit`)        | done |
| 9  | Search (`semantic` / `bm25` / `hybrid`)             | done -- RRF k=60, full ranking pipeline |
| 11 | CLI (`search`, `find-related`, `init`, `download-model`, `savings`) | done -- embedded agent-search.md resource; built-in HTTPS downloader |
| 12 | MCP server                                          | done -- `ModelContextProtocol` SDK + stdio transport |
| 10 | Default static-embedding encoder (`PotionCodeEncoder`) | done -- pure-C# loader for model2vec / sentence-transformers folder layout; WordPiece tokenizer hand-rolled; F32 and F64 weight tensors supported |
| 13 | Remove Python sources, update root README           | done -- parity verified; Python source, tests, and tooling removed |

## What's not ported

- **`benchmarks/`** -- explicitly out of scope for the .NET port. Stays Python.
- **Code-aware chunking for languages other than C# and C++** -- `Chunker.ChunkSource`
  uses Roslyn for `.cs`, tree-sitter for `.cpp`, and falls back to the line-based
  chunker for everything else. Adding more grammars is a per-language follow-up
  (just add the `tree-sitter-<lang>` NuGet, a `tree_sitter_<lang>()` DllImport,
  and a splittable-kinds whitelist).
- **SHA-256 verification on model download** -- `semble download-model` fetches
  the three required files over TLS but does not yet verify their SHA-256 hashes
  against the upstream LFS manifest. Connections are TLS-verified by
  `HttpClient`; hash pinning is a follow-up.

## Running the binaries

Download the default encoder once (requires internet; saves to `~/.cache/semble/`):

```bash
dotnet run --project dotnet/src/Semble.Cli -- download-model
```

Then:

```bash
# CLI
dotnet run --project dotnet/src/Semble.Cli -- init
dotnet run --project dotnet/src/Semble.Cli -- search "auth flow" path/to/repo
dotnet run --project dotnet/src/Semble.Cli -- savings path/to/repo   # indexed files, chunks, tokens

# MCP server (stdio)
dotnet run --project dotnet/src/Semble.Mcp -- path/to/repo
```

To use a different on-disk model, set `SEMBLE_MODEL_PATH=/path/to/model-folder`.

## Reference parity

Golden values cross-checked against the Python implementation are encoded as
test assertions:
- `Tokens.Tokenize` -- every docstring example + edge cases (`_foo`, `_foo_bar`,
  `XMLParser`, `getHTTPResponse`, ...).
- `Boosting.ApplyQueryBoost` -- exact post-boost scores 2.75 / 1.625 / 2.75 for
  symbol / NL-with-symbol / namespace-qualified queries.
- `Bm25.GetScores` -- six-decimal-place parity with `bm25s.BM25(method='lucene')`.
- `Sparse.EnrichForBm25` -- string-equality parity for five representative paths.
- `Formatting.FormatResults` -- byte-for-byte parity with the Python output.
