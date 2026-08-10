using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace CmuxGui.Services;

/// <summary>
/// Explorer context-menu entries for folders.
///
/// The Windows 11 <c>IExplorerCommand</c> reads a per-user enabled flag, while
/// classic verb keys remain available on Windows 10 and under "Show more
/// options". Everything is scoped to the signed-in user.
/// </summary>
public static class ShellIntegration
{
    private const string OpenWindowVerb = "cmuxOpenWindow";
    private const string OpenWorkspaceVerb = "cmuxOpenWorkspace";
    private const string StateKey = @"Software\cmux\ShellIntegration";
    private const string EnabledValue = "Enabled";

    /// <summary>Right-clicking a folder, and right-clicking empty space inside one.</summary>
    private static readonly string[] Roots =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
    };

    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var state = Registry.CurrentUser.OpenSubKey(StateKey);
                if (state?.GetValue(EnabledValue) is int enabled && enabled != 0)
                {
                    return true;
                }

                using var key = Registry.CurrentUser.OpenSubKey($@"{Roots[0]}\{OpenWindowVerb}");
                return key is not null;
            }
            catch (Exception ex)
            {
                Diag.Log($"context menu probe failed: {ex.Message}");
                return false;
            }
        }
    }

    public static bool Register(string openWindowLabel, string openWorkspaceLabel)
    {
        var exe = ExecutablePath();
        Diag.Log($"context menu register exe='{exe}'");
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            var icon = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(exe) ?? string.Empty,
                "Assets",
                "AppIcon.ico");
            if (!System.IO.File.Exists(icon))
            {
                icon = exe;
            }
            foreach (var root in Roots)
            {
                // "%V" is the folder that was clicked. For the Background case
                // it is the open folder itself, which "%1" would not give.
                WriteVerb(root, OpenWindowVerb, openWindowLabel, exe, icon, "--new-window");
                WriteVerb(root, OpenWorkspaceVerb, openWorkspaceLabel, exe, icon, "--new-workspace");
            }

            using var state = Registry.CurrentUser.CreateSubKey(StateKey);
            if (state is null)
            {
                return false;
            }
            state.SetValue(EnabledValue, 1, RegistryValueKind.DWord);
            NotifyShell();
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"context menu register failed: {ex.Message}");
            return false;
        }
    }

    public static bool Unregister()
    {
        try
        {
            foreach (var root in Roots)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{OpenWindowVerb}", throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{OpenWorkspaceVerb}", throwOnMissingSubKey: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false);
            NotifyShell();
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"context menu unregister failed: {ex.Message}");
            return false;
        }
    }

    private static void WriteVerb(
        string root,
        string verb,
        string label,
        string exe,
        string icon,
        string argument)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{verb}");
        if (key is null)
        {
            return;
        }
        // MUIVerb is the displayed text; Icon gives Explorer the app glyph.
        key.SetValue("MUIVerb", label);
        key.SetValue("Icon", $"\"{icon}\",0");

        using var command = key.CreateSubKey("command");
        command?.SetValue(string.Empty, $"\"{exe}\" {argument} \"%V\"");
    }

    /// <summary>
    /// Path to the real executable.
    ///
    /// <c>Assembly.Location</c> is empty for single-file publishes, so the
    /// process path is used instead.
    /// </summary>
    private static string ExecutablePath()
    {
        try
        {
            return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch (Exception ex)
        {
            Diag.Log($"executable path lookup failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>Tell Explorer to re-read associations so the entry appears immediately.</summary>
    private static void NotifyShell()
    {
        const int ShcneAssocChanged = 0x08000000;
        const uint ShcnfIdList = 0x0000;
        NativeMethods.SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
