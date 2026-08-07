using System;
using System.IO;

namespace CmuxGui;

/// <summary>
/// File logging for a windowed app.
///
/// A WinExe has no console, and WinUI swallows exceptions thrown inside control
/// lifecycle callbacks, so a blank canvas is otherwise indistinguishable from a
/// control that never ran. This makes startup observable.
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux-gui.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }
}
