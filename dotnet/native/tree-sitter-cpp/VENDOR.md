tree-sitter-cpp (the C++ grammar)

Source: https://github.com/tree-sitter/tree-sitter-cpp
Tag:    v0.23.4
Commit: f41e1a044c8a84ea9fa8577fdd2eab92ec96de02

Vendored from upstream `src/`. The bindings/, examples/, queries/,
tests/, Cargo / Go / Python / npm packaging, and grammar.js (the source
that generates parser.c) are intentionally not vendored — Semble only
consumes the generated parser and the hand-written scanner.

`src/parser.c` is large (~16MB) because it is the generated state
machine for the grammar; this is the upstream artifact that ships in
every distribution of tree-sitter-cpp.

Files in this directory other than `LICENSE` and this `VENDOR.md` are
copied verbatim from upstream at the commit above.

Updating: regenerate from grammar.js upstream, replace the contents of
`src/`, update the tag/commit recorded above, and re-run security
review on the diff.

License: MIT (see `LICENSE`).
