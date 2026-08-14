using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private static string? _agentHookProvider;
    private static string? _agentHookTerminal;
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
        // WinUI turns an unhandled exception into a stowed exception with no
        // console and no window, which is indistinguishable from the app never
        // having been launched. Name the culprit before the process dies.
        UnhandledException += (_, args) =>
            Diag.Log($"unhandled exception: {args.Exception}");
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

            if (string.Equals(args[i], "--agent-hook", StringComparison.OrdinalIgnoreCase)
                && i + 2 < args.Length)
            {
                _agentHookProvider = args[i + 1];
                _agentHookTerminal = args[i + 2];
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

        if (_agentHookProvider is not null && _agentHookTerminal is not null)
        {
            ForwardAgentHook(_agentHookProvider, _agentHookTerminal);
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

        MainWindow window;
        try
        {
            window = new MainWindow(sessionName, LaunchFolder, persistentSession);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Environment.ExitCode = 1;
            Exit();
            return;
        }
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

    /// <summary>
    /// Surface a startup failure the only way a windowless app can.
    ///
    /// This runs before the first window exists, so a <c>ContentDialog</c> has
    /// no <c>XamlRoot</c> to attach to. Without a message box the process simply
    /// vanishes, which reads as "the app did not launch" with nothing to act on.
    /// </summary>
    private static void ReportStartupFailure(Exception error)
    {
        Diag.Log($"startup failed: {error}");
        try
        {
            MessageBox(
                IntPtr.Zero,
                $"{Loc.S("Startup_FailedBody")}{Environment.NewLine}{Environment.NewLine}"
                    + $"{error.Message}{Environment.NewLine}{Environment.NewLine}{Diag.Path}",
                Loc.S("Startup_FailedTitle"),
                MessageBoxIconError | MessageBoxSetForeground);
        }
        catch (Exception ex)
        {
            Diag.Log($"startup failure report failed: {ex.Message}");
        }
    }

    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

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
            writer.Write((byte)1);
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

    private static void ForwardAgentHook(string provider, string terminal)
    {
        try
        {
            var payload = Console.In.ReadToEnd();
            if (payload.Length is 0 or > 65536)
            {
                return;
            }
            using var document = JsonDocument.Parse(payload);
            using var pipe = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.None);
            pipe.Connect(750);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            writer.Write((byte)2);
            writer.Write(provider);
            writer.Write(terminal);
            writer.Write(payload);
            writer.Flush();
        }
        catch
        {
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
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
                switch (reader.ReadByte())
                {
                    case 1:
                    {
                        var folder = reader.ReadBoolean() ? reader.ReadString() : null;
                        window.DispatcherQueue.TryEnqueue(() => window.HandleActivation(folder));
                        break;
                    }
                    case 2:
                    {
                        var provider = reader.ReadString();
                        var terminal = reader.ReadString();
                        var payload = reader.ReadString();
                        window.DispatcherQueue.TryEnqueue(
                            () => window.HandleAgentHook(provider, terminal, payload));
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A pipe name another process still owns fails on every attempt,
                // so retrying immediately would spin a core and flood the log.
                Diag.Log($"activation listener failed: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
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
