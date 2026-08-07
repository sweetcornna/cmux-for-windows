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

    [StructLayout(LayoutKind.Sequential)]
    public struct Cell
    {
        public uint Ch;
        public uint Fg;
        public uint Bg;
        public ushort Attrs;
        public byte Width;
        public byte Reserved;
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
        public byte Reserved0;
        public byte Reserved1;
        public uint DefaultFg;
        public uint DefaultBg;
    }

    [LibraryImport(Library, EntryPoint = "cmux_session_new")]
    public static partial IntPtr SessionNew(ushort cols, ushort rows);

    [LibraryImport(Library, EntryPoint = "cmux_session_free")]
    public static partial void SessionFree(IntPtr session);

    [LibraryImport(Library, EntryPoint = "cmux_session_write")]
    public static partial int SessionWrite(IntPtr session, ReadOnlySpan<byte> bytes, nuint len);

    [LibraryImport(Library, EntryPoint = "cmux_session_resize")]
    public static partial int SessionResize(IntPtr session, ushort cols, ushort rows);

    [LibraryImport(Library, EntryPoint = "cmux_session_snapshot")]
    public static partial int SessionSnapshot(
        IntPtr session,
        Span<Cell> cells,
        nuint capacity,
        out Frame frame);
}
