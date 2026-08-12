using System;
using System.Runtime.InteropServices;

namespace CmuxGui.Interop;

/// <summary>
/// P/Invoke surface over <c>cmux_ffi.dll</c>, the C ABI around the same
/// cmux-tui engine the TUI runs. The shell never talks to a PTY directly.
/// </summary>
internal static partial class CmuxNative
{
    private const string Library = "cmux_ffi";

    /// <summary>Sentinel meaning "no explicit colour; use the frame default".</summary>
    public const uint NoColor = 0xFFFF_FFFF;
    public const int ErrorCapacity = -3;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Cell
    {
        public uint Ch;
        public uint Fg;
        public uint Bg;
        public ushort Attrs;
        public byte Width;
        public byte Underline;
        public byte RowFlags;
        public fixed byte TextUtf8[32];
    }

    public static unsafe string TextOf(in Cell cell)
    {
        fixed (byte* p = cell.TextUtf8)
        {
            return DecodeUtf8(p, 32);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public ushort Cols;
        public ushort Rows;
        public ushort CursorCol;
        public ushort CursorRow;
        public byte CursorVisible;
        public byte Dirty;
        public byte CursorShape;
        public byte CursorBlink;
        public uint DefaultFg;
        public uint DefaultBg;
        public uint CursorColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Presentation
    {
        public uint SelectionBackground;
        public uint SelectionForeground;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Workspace
    {
        public ulong Id;
        public byte Active;
        private fixed byte _reserved[7];
        public fixed byte NameUtf8[256];
        public fixed byte PublicIdUtf8[64];
    }

    public static unsafe string NameOf(in Workspace workspace)
    {
        fixed (byte* p = workspace.NameUtf8)
        {
            return DecodeUtf8(p, 256);
        }
    }

    public static unsafe string PublicIdOf(in Workspace workspace)
    {
        fixed (byte* p = workspace.PublicIdUtf8)
        {
            return DecodeUtf8(p, 64);
        }
    }

    private static unsafe string DecodeUtf8(byte* value, int capacity)
    {
        var span = new ReadOnlySpan<byte>(value, capacity);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(span[..(end < 0 ? capacity : end)]);
    }

    /// <summary>Appearance read from the user's Ghostty config.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Theme
    {
        public uint Background;
        public uint Foreground;
        public uint Cursor;
        public uint SelectionBackground;
        public uint SelectionForeground;
        public float FontSize;
        public fixed byte FontFamilyUtf8[128];
        public byte Loaded;
        private byte _r0;
        private byte _r1;
        private byte _r2;
    }

    [LibraryImport(Library, EntryPoint = "cmux_theme_load")]
    public static partial int ThemeLoad(out Theme theme);

    /// <summary>Decode the NUL-terminated font name, or empty if unset.</summary>
    public static unsafe string FontFamilyOf(in Theme theme)
    {
        fixed (byte* p = theme.FontFamilyUtf8)
        {
            var span = new ReadOnlySpan<byte>(p, 128);
            var end = span.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(span[..(end < 0 ? 128 : end)]);
        }
    }

    [LibraryImport(Library, EntryPoint = "cmux_mux_open")]
    public static partial IntPtr MuxOpen();

    [LibraryImport(Library, EntryPoint = "cmux_mux_open_named")]
    public static partial IntPtr MuxOpenNamed(ReadOnlySpan<byte> name, nuint nameLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_open_transient_named")]
    public static partial IntPtr MuxOpenTransientNamed(ReadOnlySpan<byte> name, nuint nameLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_free")]
    public static partial void MuxFree(IntPtr mux);

    [LibraryImport(Library, EntryPoint = "cmux_mux_last_error")]
    public static partial int MuxLastError(IntPtr mux, Span<byte> buffer, nuint capacity);

    [LibraryImport(Library, EntryPoint = "cmux_mux_apply_theme_text")]
    public static partial int MuxApplyThemeText(
        IntPtr mux,
        ReadOnlySpan<byte> text,
        nuint len);

    [LibraryImport(Library, EntryPoint = "cmux_mux_set_cell_pixel_size")]
    public static partial int MuxSetCellPixelSize(IntPtr mux, ushort widthPx, ushort heightPx);

    [LibraryImport(Library, EntryPoint = "cmux_mux_presentation")]
    public static partial int MuxPresentation(IntPtr mux, out Presentation presentation);

    [LibraryImport(Library, EntryPoint = "cmux_mux_resource_request_json")]
    public static partial int MuxResourceRequestJson(
        IntPtr mux,
        ReadOnlySpan<byte> request,
        nuint requestLen,
        out IntPtr response);

    [LibraryImport(Library, EntryPoint = "cmux_json_response_copy")]
    public static partial int JsonResponseCopy(IntPtr response, Span<byte> buffer, nuint capacity);

    [LibraryImport(Library, EntryPoint = "cmux_json_response_free")]
    public static partial void JsonResponseFree(IntPtr response);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_count")]
    public static partial int MuxWorkspaceCount(IntPtr mux);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_get")]
    public static partial int MuxWorkspaceGet(IntPtr mux, nuint index, out Workspace workspace);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_create")]
    public static partial ulong MuxWorkspaceCreate(IntPtr mux, ReadOnlySpan<byte> name, nuint nameLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_select")]
    public static partial int MuxWorkspaceSelect(IntPtr mux, ulong workspace);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_close")]
    public static partial int MuxWorkspaceClose(IntPtr mux, ulong workspace);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_open")]
    public static partial IntPtr MuxWorkspaceOpen(
        IntPtr mux,
        ulong workspace,
        ushort cols,
        ushort rows,
        ReadOnlySpan<byte> cwd,
        nuint cwdLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_snapshot_json")]
    public static partial int MuxSnapshotJson(IntPtr mux, Span<byte> buffer, nuint capacity);

    [LibraryImport(Library, EntryPoint = "cmux_mux_tab_open")]
    public static partial IntPtr MuxTabOpen(
        IntPtr mux,
        ReadOnlySpan<byte> tab,
        nuint tabLen,
        ushort cols,
        ushort rows);

    [LibraryImport(Library, EntryPoint = "cmux_mux_workspace_create_terminal")]
    public static partial int MuxWorkspaceCreateTerminal(
        IntPtr mux,
        ReadOnlySpan<byte> workspace,
        nuint workspaceLen,
        ReadOnlySpan<byte> cwd,
        nuint cwdLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_pane_create_terminal")]
    public static partial int MuxPaneCreateTerminal(
        IntPtr mux,
        ReadOnlySpan<byte> pane,
        nuint paneLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_pane_split")]
    public static partial int MuxPaneSplit(
        IntPtr mux,
        ReadOnlySpan<byte> pane,
        nuint paneLen,
        byte direction);

    [LibraryImport(Library, EntryPoint = "cmux_mux_pane_focus")]
    public static partial int MuxPaneFocus(IntPtr mux, ReadOnlySpan<byte> pane, nuint paneLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_pane_close")]
    public static partial int MuxPaneClose(IntPtr mux, ReadOnlySpan<byte> pane, nuint paneLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_tab_select")]
    public static partial int MuxTabSelect(IntPtr mux, ReadOnlySpan<byte> tab, nuint tabLen);

    [LibraryImport(Library, EntryPoint = "cmux_mux_tab_close")]
    public static partial int MuxTabClose(IntPtr mux, ReadOnlySpan<byte> tab, nuint tabLen);

    [LibraryImport(Library, EntryPoint = "cmux_session_new")]
    public static partial IntPtr SessionNew(ushort cols, ushort rows);

    [LibraryImport(Library, EntryPoint = "cmux_session_new_in")]
    public static partial IntPtr SessionNewIn(ushort cols, ushort rows, ReadOnlySpan<byte> cwd, nuint cwdLen);

    [LibraryImport(Library, EntryPoint = "cmux_session_scroll")]
    public static partial int SessionScroll(IntPtr session, int deltaRows);

    [LibraryImport(Library, EntryPoint = "cmux_session_scroll_to_bottom")]
    public static partial int SessionScrollToBottom(IntPtr session);

    [LibraryImport(Library, EntryPoint = "cmux_session_free")]
    public static partial void SessionFree(IntPtr session);

    [LibraryImport(Library, EntryPoint = "cmux_session_write")]
    public static partial int SessionWrite(IntPtr session, ReadOnlySpan<byte> bytes, nuint len);

    [LibraryImport(Library, EntryPoint = "cmux_session_paste")]
    public static partial int SessionPaste(IntPtr session, ReadOnlySpan<byte> bytes, nuint len);

    [LibraryImport(Library, EntryPoint = "cmux_session_key")]
    public static partial int SessionKey(IntPtr session, ReadOnlySpan<byte> chord, nuint chordLen);

    [LibraryImport(Library, EntryPoint = "cmux_session_key_event")]
    public static partial int SessionKeyEvent(
        IntPtr session,
        ReadOnlySpan<byte> chord,
        nuint chordLen,
        byte action);

    [LibraryImport(Library, EntryPoint = "cmux_session_mouse")]
    public static partial int SessionMouse(
        IntPtr session,
        byte kind,
        ushort column,
        ushort row,
        byte button,
        byte modifiers,
        int deltaRows);

    [LibraryImport(Library, EntryPoint = "cmux_session_resize")]
    public static partial int SessionResize(IntPtr session, ushort cols, ushort rows);

    [LibraryImport(Library, EntryPoint = "cmux_session_snapshot")]
    public static partial int SessionSnapshot(
        IntPtr session,
        Span<Cell> cells,
        nuint capacity,
        out Frame frame);
}
