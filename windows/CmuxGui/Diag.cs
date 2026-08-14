using System;
using System.IO;
using System.Text;

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

    /// <summary>A signature written mid-file would be read back as garbage.</summary>
    private static readonly UTF8Encoding Utf8WithoutSignature = new(false);

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux-gui.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                // Several cmux processes share this file: an Explorer verb logs
                // its launch while the main window is logging too. Appending
                // without write sharing makes the loser drop the line outright,
                // which is exactly when a failed launch needs recording.
                using var stream = new FileStream(
                    Path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8WithoutSignature);
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }
}
