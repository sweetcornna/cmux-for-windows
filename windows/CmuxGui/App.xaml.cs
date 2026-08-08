using System;
using System.Linq;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;

namespace CmuxGui;

public partial class App : Application
{
    private Window? _window;
    private static bool _registerShell;
    private static bool _repairShell;
    private static bool _unregisterShell;

    /// <summary>
    /// HWND of the main window.
    ///
    /// Unpackaged apps have no implicit window for pickers and dialogs to
    /// parent to, so they have to be handed one explicitly.
    /// </summary>
    public static IntPtr MainWindowHandle { get; private set; }

    /// <summary>Folder passed by an Explorer context-menu verb, if any.</summary>
    public static string? LaunchFolder { get; private set; }

    /// <summary>True when launched with --new-workspace rather than --new-window.</summary>
    public static bool LaunchAsWorkspace { get; private set; }

    public App()
    {
        // The language override has to be set before any UI resolves strings,
        // which rules out doing it once a window exists.
        var language = AppSettings.Current.Language;
        if (!string.IsNullOrWhiteSpace(language))
        {
            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = language;
            }
            catch (Exception ex)
            {
                Diag.Log($"language override '{language}' rejected: {ex.Message}");
            }
        }

        ParseCommandLine();
        InitializeComponent();
    }

    /// <summary>
    /// Read the verbs Explorer and the uninstaller invoke.
    /// </summary>
    private static void ParseCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--register-shell", StringComparison.OrdinalIgnoreCase))
            {
                _registerShell = true;
                return;
            }
            if (string.Equals(args[i], "--repair-shell", StringComparison.OrdinalIgnoreCase))
            {
                _repairShell = true;
                return;
            }
            if (string.Equals(args[i], "--unregister-shell", StringComparison.OrdinalIgnoreCase))
            {
                _unregisterShell = true;
                return;
            }

            var isWindow = string.Equals(args[i], "--new-window", StringComparison.OrdinalIgnoreCase);
            var isWorkspace = string.Equals(args[i], "--new-workspace", StringComparison.OrdinalIgnoreCase);
            if ((isWindow || isWorkspace) && i + 1 < args.Length)
            {
                LaunchFolder = args[i + 1].Trim('"');
                LaunchAsWorkspace = isWorkspace;
                Diag.Log($"launch verb={(isWorkspace ? "workspace" : "window")} folder='{LaunchFolder}'");
                return;
            }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_registerShell || _repairShell)
        {
            var ok = (_repairShell && !ShellIntegration.IsRegistered)
                || ShellIntegration.Register(
                    Loc.S("ContextMenu_OpenWindow"),
                    Loc.S("ContextMenu_OpenWorkspace"));
            Environment.ExitCode = ok ? 0 : 1;
            Exit();
            return;
        }
        if (_unregisterShell)
        {
            Environment.ExitCode = ShellIntegration.Unregister() ? 0 : 1;
            Exit();
            return;
        }

        var window = new MainWindow();
        _window = window;
        MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activate();
    }
}
