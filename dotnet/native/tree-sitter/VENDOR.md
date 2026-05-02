tree-sitter (the parsing library runtime)

Source: https://github.com/tree-sitter/tree-sitter
Tag:    v0.25.10
Commit: da6fe9beb4f7f67beb75914ca8e0d48ae48d6406

Vendored from upstream `lib/` (the runtime). The CLI, docs, tests, Rust
bindings, Cargo metadata, and other top-level files are intentionally
not vendored — Semble only consumes the C runtime.

Files in this directory other than `LICENSE` and this `VENDOR.md` are
copied verbatim from upstream at the commit above.

Updating: replace the contents of `lib/` from a fresh upstream checkout,
update the tag/commit recorded above, and re-run security review on the
diff.

License: MIT (see `LICENSE`).
