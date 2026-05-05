## 2026-05-05 [branch: claude/dotnet-migration-fwvd9] - Complete strangler port of Semble Python to .NET 8, 12 chunks + extensions

### Progress
- Completed Chunks 0-10 and beyond: Full side-by-side .NET strangler migration with 196+ passing tests (CLI, library, MCP). Both toolchains run in parallel; Python src/ untouched. Every commit green; CI matrix covers Ubuntu/macOS/Windows.
- Chunks 1-9 deliver core parity: types, tokenisation, ranking (BM25/boosting/penalties), file walk with gitignore, line-based chunker, dense backend, index orchestration, hybrid search. All tunable constants match Python exactly (alpha values, RRF k=60, saturation decay).
- Chunk 10 (ONNX encoder): Hand-rolled pure-C# IEncoder implementation. SafeTensors reader + HF tokenizer.json parser (WordPiece, BertNormalizer, lowercasing, subword continuation ##). Mirrors model2vec._encode_batch step-for-step. Model resolution chain: explicit path > SEMBLE_MODEL_PATH env > ~/.cache/semble/. DirectoryNotFoundException tells users how to download via huggingface-cli. Synthetic 6-token fixture drives 11 test cases covering every code path; real model validation deferred to user.
- Chunk 14+ (Post-migration work): Added Locator discriminated-union type on Chunk to represent non-line coordinates (Pages for PDF, Slide for PPTX, Sheet for XLSX, Heading for markdown). Backwards-compatible; chunks without Locator render as before. Location formatter is now format-aware (paper.pdf:p3-5, deck.pptx:slide12, notes.md:Architecture/Storage:42-58).
- Markdown chunker (Chunk 14a): 150-line heading/paragraph-aware splitter. Handles ATX headings at any level, respects fenced-code-block immunity (# inside ``` is not a heading), greedy-merges adjacent same-path sections, falls back to paragraph then line-windows when a section exceeds budget. Emits Locator.Heading with path like ["Architecture", "Storage"]. Language detection .md > "markdown" already in place via FILE_TYPES.
- ITextExtractor seam (Chunk 14b): Pluggable binary-format backends. Shell-out extractors (SubprocessExtractor base, PdftotextExtractor, MarkItDownExtractor) discovered on PATH with availability caching. TextExtractors registry picks best-available per extension (markitdown > pdftotext for PDF). Chunker.ChunkFile now tries extractor first; plain-text files fall through to File.ReadAllText. No bundled dependencies; operators choose by what's installed.
- Vendored tree-sitter (native + C++ grammar): 18MB tree-sitter-cpp (parser.c + scanner.c) + 900KB tree-sitter runtime libs. Pinned to upstream tags (v0.25.10, v0.23.4) with VENDOR.md docs. CMake build infrastructure for platform-specific natives (linux-x64, osx-arm64, win-x64). Not yet integrated into Chunker; infrastructure ready.

### Commits
- b666bd7 test(dotnet): self-indexing integration tests over Semble source
- a7e86fd feat(dotnet): pure-managed .docx extractor via DocumentFormat.OpenXml
- bff6d6b feat(dotnet): page-aware PDF chunker with Locator.Pages
- 7ba1432 feat(dotnet): ITextExtractor seam + pdftotext / markitdown backends
- ab93f70 feat(dotnet): markdown heading/paragraph-aware chunker

### Files Modified
- dotnet/Directory.Build.props
- dotnet/Semble.sln
- dotnet/src/Semble/Types.cs
- dotnet/src/Semble/Tokens.cs
- dotnet/src/Semble/Ranking/Weighting.cs
- dotnet/src/Semble/Ranking/Boosting.cs
- dotnet/src/Semble/Ranking/Penalties.cs
- dotnet/src/Semble/Index/FileWalker.cs
- dotnet/src/Semble/Index/Chunker.cs
- dotnet/src/Semble/Index/MarkdownChunker.cs
- dotnet/src/Semble/Index/ITextExtractor.cs
- dotnet/src/Semble/Index/TextExtractors.cs
- dotnet/src/Semble/Index/Extractors/SubprocessExtractor.cs
- dotnet/src/Semble/Index/Extractors/PdftotextExtractor.cs
- dotnet/src/Semble/Index/Extractors/MarkItDownExtractor.cs
- dotnet/src/Semble/Index/Bm25.cs
- dotnet/src/Semble/Index/Sparse.cs
- dotnet/src/Semble/Index/DenseBackend.cs
- dotnet/src/Semble/Index/Create.cs
- dotnet/src/Semble/Index/SembleIndex.cs
- dotnet/src/Semble/Search.cs
- dotnet/src/Semble/Formatting.cs
- dotnet/src/Semble/Encoders/SafeTensors.cs
- dotnet/src/Semble/Encoders/HuggingFaceTokenizer.cs
- dotnet/src/Semble/Encoders/PotionCodeEncoder.cs
- dotnet/src/Semble.Cli/Program.cs
- dotnet/src/Semble.Mcp/Program.cs
- dotnet/src/Semble.Mcp/SembleMcpServer.cs
- dotnet/tests/Semble.Tests/TypesTests.cs
- dotnet/tests/Semble.Tests/TokensTests.cs
- dotnet/tests/Semble.Tests/RankingTests.cs
- dotnet/tests/Semble.Tests/FileWalkerTests.cs
- dotnet/tests/Semble.Tests/ChunkerTests.cs
- dotnet/tests/Semble.Tests/MarkdownChunkerTests.cs
- dotnet/tests/Semble.Tests/Bm25Tests.cs
- dotnet/tests/Semble.Tests/DenseBackendTests.cs
- dotnet/tests/Semble.Tests/SearchTests.cs
- dotnet/tests/Semble.Tests/IndexTests.cs
- dotnet/tests/Semble.Tests/PotionCodeEncoderTests.cs
- dotnet/tests/Semble.Tests/Fixtures.cs
- dotnet/tests/Semble.Cli.Tests/CliTests.cs
- dotnet/tests/Semble.Mcp.Tests/McpTests.cs
- dotnet/native/tree-sitter/lib/ (vendored)
- dotnet/native/tree-sitter-cpp/src/ (vendored)
- dotnet/native/VENDOR.md (both)
- dotnet/README.md
- .github/workflows/dotnet.yaml
- .gitignore
