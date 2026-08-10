using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Globalization;
using Windows.UI.ViewManagement;

namespace CmuxGui;

public partial class App : Application
{
    private static SolidColorBrush? _accentBrush;

    internal static SolidColorBrush AccentBrush => _accentBrush
        ?? throw new InvalidOperationException("The accent brush is not initialized.");

    private Window? _window;
    private static bool _registerShell;
    private static bool _repairShell;
    private static bool _unregisterShell;
    private static bool _launchAsNewWindow;
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _activationCancellation;

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

    internal static void InitializeAccentBrush()
    {
        _accentBrush ??= new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    }

    internal static void ApplyAccentColor(Windows.UI.Color? accent)
    {
        AccentBrush.Color = accent
            ?? new UISettings().GetColorValue(UIColorType.Accent);
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
                _launchAsNewWindow = isWindow;
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

        var sessionName = "cmux-gui";
        var persistentSession = true;
        if (!_launchAsNewWindow)
        {
            var mutexName = $"Local\\cmux-for-windows-{Environment.UserName}";
            _instanceMutex = new Mutex(true, mutexName, out var ownsMainInstance);
            if (!ownsMainInstance)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                if (ForwardActivation(LaunchAsWorkspace ? LaunchFolder : null))
                {
                    Exit();
                    return;
                }
                sessionName = $"cmux-gui-window-{Guid.NewGuid():N}";
                persistentSession = false;
                _launchAsNewWindow = true;
            }
        }
        else
        {
            sessionName = $"cmux-gui-window-{Guid.NewGuid():N}";
            persistentSession = false;
        }

        var window = new MainWindow(sessionName, LaunchFolder, persistentSession);
        _window = window;
        MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Closed += OnMainWindowClosed;
        window.Activate();
        if (!_launchAsNewWindow)
        {
            _activationCancellation = new CancellationTokenSource();
            _ = ListenForActivationsAsync(window, _activationCancellation.Token);
        }
    }

    private static string ActivationPipeName => $"cmux-for-windows-{Environment.UserName}";

    private static bool ForwardActivation(string? folder)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.None);
            pipe.Connect(5000);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            writer.Write(folder is not null);
            if (folder is not null)
            {
                writer.Write(folder);
            }
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log($"activation forwarding failed: {ex.Message}");
            return false;
        }
    }

    private static async Task ListenForActivationsAsync(
        MainWindow window,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
                var folder = reader.ReadBoolean() ? reader.ReadString() : null;
                window.DispatcherQueue.TryEnqueue(() => window.HandleActivation(folder));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diag.Log($"activation listener failed: {ex.Message}");
            }
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is Window window)
        {
            window.Closed -= OnMainWindowClosed;
        }
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }
}
