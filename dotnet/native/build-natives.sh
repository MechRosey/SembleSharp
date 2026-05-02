#!/bin/sh
# Build the vendored tree-sitter and tree-sitter-cpp shared libraries for the
# host platform. Invoked by Semble.csproj at build time and also runnable by
# hand for diagnostics.
#
# Usage:  build-natives.sh <output-dir>
#
# Output filenames:
#   Linux   -> libtree-sitter.so       libtree-sitter-cpp.so
#   macOS   -> libtree-sitter.dylib    libtree-sitter-cpp.dylib

set -eu

if [ -z "${1-}" ]; then
    echo "usage: $0 <output-dir>" >&2
    exit 2
fi

OUTDIR=$1
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)

case "$(uname -s)" in
    Linux*)  EXT=so ;;
    Darwin*) EXT=dylib ;;
    *)
        echo "build-natives.sh: unsupported OS '$(uname -s)'" >&2
        exit 1
        ;;
esac

mkdir -p "$OUTDIR"

# Pick a compiler. CC overrides; otherwise prefer cc, fall back to clang/gcc.
if [ -n "${CC-}" ]; then
    : # use CC from the environment
elif command -v cc >/dev/null 2>&1; then
    CC=cc
elif command -v clang >/dev/null 2>&1; then
    CC=clang
elif command -v gcc >/dev/null 2>&1; then
    CC=gcc
else
    echo "build-natives.sh: no C compiler found (set CC or install cc/clang/gcc)" >&2
    exit 1
fi

CFLAGS_COMMON="-shared -fPIC -O2 -fvisibility=hidden -fno-strict-aliasing"

# tree-sitter runtime — single-file amalgamation in lib/src/lib.c.
"$CC" $CFLAGS_COMMON \
    -I "$SCRIPT_DIR/tree-sitter/lib/include" \
    -I "$SCRIPT_DIR/tree-sitter/lib/src" \
    "$SCRIPT_DIR/tree-sitter/lib/src/lib.c" \
    -o "$OUTDIR/libtree-sitter.$EXT"

# tree-sitter-cpp grammar — generated parser plus the hand-written external
# scanner. Suppress warnings: parser.c is generated and fires lots of noise.
"$CC" $CFLAGS_COMMON -w \
    -I "$SCRIPT_DIR/tree-sitter-cpp/src" \
    "$SCRIPT_DIR/tree-sitter-cpp/src/parser.c" \
    "$SCRIPT_DIR/tree-sitter-cpp/src/scanner.c" \
    -o "$OUTDIR/libtree-sitter-cpp.$EXT"

echo "build-natives.sh: built $OUTDIR/libtree-sitter.$EXT and $OUTDIR/libtree-sitter-cpp.$EXT"
