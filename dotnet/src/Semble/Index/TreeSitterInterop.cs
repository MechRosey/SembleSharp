using System.Runtime.InteropServices;
using System.Text;

namespace Semble.Index;

/// <summary>
/// Minimal managed wrapper over the vendored tree-sitter C runtime. Only the
/// surface Semble's chunker actually uses is exposed: parser create/delete,
/// parse a UTF-8 byte buffer, walk the resulting tree by node, read kind and
/// byte offsets.
/// </summary>
/// <remarks>
/// Native libraries (`libtree-sitter` and one library per grammar) are built
/// from vendored source by `dotnet/native/build-natives.{sh,cmd}` and staged
/// at <c>runtimes/&lt;rid&gt;/native/</c> so the .NET asset graph picks them up.
/// </remarks>
internal static class TreeSitterInterop
{
    private const string Lib = "tree-sitter";

    // TSNode is a 24-byte struct on 64-bit platforms; see lib/include/tree_sitter/api.h:
    //   typedef struct TSNode {
    //     uint32_t context[4];
    //     const void *id;
    //     const TSTree *tree;
    //   } TSNode;
    [StructLayout(LayoutKind.Sequential)]
    public struct TSNode
    {
        public uint Context0;
        public uint Context1;
        public uint Context2;
        public uint Context3;
        public IntPtr Id;
        public IntPtr Tree;
    }

    [DllImport(Lib)] public static extern IntPtr ts_parser_new();
    [DllImport(Lib)] public static extern void ts_parser_delete(IntPtr parser);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool ts_parser_set_language(IntPtr parser, IntPtr language);

    [DllImport(Lib)]
    public static extern IntPtr ts_parser_parse_string_encoding(
        IntPtr parser, IntPtr oldTree, byte[] sourceUtf8, uint length, int encoding);

    [DllImport(Lib)] public static extern void ts_tree_delete(IntPtr tree);
    [DllImport(Lib)] public static extern TSNode ts_tree_root_node(IntPtr tree);

    [DllImport(Lib)] public static extern uint ts_node_start_byte(TSNode node);
    [DllImport(Lib)] public static extern uint ts_node_end_byte(TSNode node);
    [DllImport(Lib)] public static extern uint ts_node_child_count(TSNode node);
    [DllImport(Lib)] public static extern TSNode ts_node_child(TSNode node, uint index);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool ts_node_is_named(TSNode node);

    [DllImport(Lib)] public static extern IntPtr ts_node_type(TSNode node);

    public static string NodeKind(TSNode node)
    {
        var ptr = ts_node_type(node);
        return ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    public const int TSInputEncodingUTF8 = 0;
}

/// <summary>
/// Per-language tree-sitter grammar entrypoints. Each grammar lives in its own
/// shared library (e.g. `libtree-sitter-cpp.so`) and exposes a single
/// `tree_sitter_<lang>` C function returning an opaque <c>TSLanguage*</c>.
/// </summary>
internal static class TreeSitterCppNative
{
    [DllImport("tree-sitter-cpp")]
    public static extern IntPtr tree_sitter_cpp();
}
