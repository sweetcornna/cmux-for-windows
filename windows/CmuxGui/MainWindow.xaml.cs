using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CmuxGui.Controls;
using CmuxGui.Input;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace CmuxGui;

public sealed partial class MainWindow : Window
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmSysChar = 0x0106;
    private const uint WmLeftButtonDown = 0x0201;
    private const int WhMouseLl = 14;
    private const uint WindowSubclassId = 1;

    private delegate IntPtr WindowSubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    private delegate IntPtr LowLevelMouseProc(
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumChildWindowProc(IntPtr window, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private sealed class WorkspaceEntry
    {
        public required MuxRuntime.WorkspaceInfo Workspace { get; set; }
        public required WorkspaceView View { get; init; }
        public required NavigationViewItem Item { get; init; }
    }

    private readonly MuxRuntime _mux;
    private readonly Dictionary<string, WorkspaceEntry> _workspaces = [];
    private readonly Grid _workspaceViewsHost = new();
    private readonly string? _launchFolder;
    private WorkspaceView? _visibleWorkspace;
    private WorkspaceView? _workspaceInputTarget;
    private bool _workspaceInputBridgeActive;
    private bool _workspacePointerDown;
    private readonly WindowSubclassProc _windowSubclassProc;
    private readonly LowLevelMouseProc _lowLevelMouseProc;
    private IntPtr _windowHandle;
    private IntPtr _inputSiteHandle;
    private IntPtr _mouseHookHandle;
    private VirtualKey? _bridgeCharacterKey;
    private TerminalView? _nativePaneInputTarget;
    private long _inputBridgeDeadline;
    private readonly DispatcherTimer _inputBridgeTimer = new();
    private readonly DispatcherTimer _topologyTimer = new();
    private readonly HashSet<(VirtualKey Key, uint ScanCode, bool Extended)> _applicationKeysDown = [];
    private string _snapshotGeneration = string.Empty;
    private string _snapshotRevision = string.Empty;
    private bool _topologyPolling;
    private bool _topologyFailureLogged;
    private bool _dialogOpen;
    private int _tabCounter;
    private bool _windowActivated;
    private bool _closed;

    public MainWindow(string sessionName, string? launchFolder, bool persistentSession = true)
    {
        InitializeComponent();
        _windowSubclassProc = HandleWindowMessage;
        _lowLevelMouseProc = HandleLowLevelMouse;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WorkspaceHost.Content = _workspaceViewsHost;
        App.InitializeAccentBrush();
        NewWorkspaceButton.Foreground = App.AccentBrush;
        _launchFolder = launchFolder;
        _mux = MuxRuntime.Open(sessionName, persistentSession);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Relocalize();
        ApplyAppearance();
        AppSettings.Changed += ApplyAppearance;
        AppSettings.Changed += Relocalize;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        RootGrid.Loaded += OnRootGridLoaded;
        Nav.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnNavPointerPressed), true);
        Nav.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnNavPointerReleased), true);
        RootGrid.PreviewKeyDown += OnInputPreviewKeyDown;
        RootGrid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnInputKeyUp), true);
        RootGrid.AddHandler(
            UIElement.CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnInputCharacterReceived),
            true);
        NavSearch.TextChanged += OnNavSearchChanged;
        _inputBridgeTimer.Interval = TimeSpan.FromMilliseconds(300);
        _inputBridgeTimer.Tick += OnInputBridgeTimerTick;
        _topologyTimer.Interval = TimeSpan.FromMilliseconds(750);
        _topologyTimer.Tick += OnTopologyTick;

        RestoreWorkspaces();
        _topologyTimer.Start();
    }

    private IntPtr HandleWindowMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        var activeTarget = _workspaceInputBridgeActive
            && _workspaceInputTarget is { } candidate
            && ReferenceEquals(_visibleWorkspace, candidate)
                ? candidate
                : null;
        var acceptsApplicationInput = activeTarget is { } candidateTarget
            && AcceptsApplicationInput(candidateTarget);
        if (message is WmChar or WmSysChar && _bridgeCharacterKey is not null)
        {
            _bridgeCharacterKey = null;
            return IntPtr.Zero;
        }
        if (message is WmChar or WmSysChar)
        {
            _bridgeCharacterKey = null;
        }
        if (message is WmKeyUp or WmSysKeyUp)
        {
            var key = (VirtualKey)(uint)wParam.ToInt64();
            if (_bridgeCharacterKey == key)
            {
                _bridgeCharacterKey = null;
            }
            var status = PhysicalKeyStatusOf(lParam);
            if (_applicationKeysDown.Remove((key, status.ScanCode, status.IsExtendedKey)))
            {
                return IntPtr.Zero;
            }
        }

        if (acceptsApplicationInput && activeTarget is { } target)
        {
            if (message is WmKeyDown or WmSysKeyDown)
            {
                var key = (VirtualKey)(uint)wParam.ToInt64();
                var status = PhysicalKeyStatusOf(lParam);
                if (TryHandleShortcut(target, key, status, out var matched))
                {
                    _bridgeCharacterKey = matched && KeyCanProduceCharacter(key) ? key : null;
                    return IntPtr.Zero;
                }

                if (_nativePaneInputTarget is { } terminal
                    ? terminal.ForwardKeyDown(key, status)
                    : target.ForwardKeyDown(key, status))
                {
                    _bridgeCharacterKey = KeyCanProduceCharacter(key) ? key : null;
                    return IntPtr.Zero;
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                var key = (VirtualKey)(uint)wParam.ToInt64();
                var status = PhysicalKeyStatusOf(lParam);
                if (_nativePaneInputTarget is { } terminal
                    ? terminal.ForwardKeyUp(key, status)
                    : target.ForwardKeyUp(key, status))
                {
                    return IntPtr.Zero;
                }
            }
            else if (message is WmChar or WmSysChar)
            {
                var keyCode = (uint)wParam.ToInt64();
                if (_nativePaneInputTarget is { } terminal
                    ? terminal.ForwardCharacterReceived(keyCode)
                    : target.ForwardCharacterReceived(keyCode))
                {
                    return IntPtr.Zero;
                }
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool AcceptsApplicationInput(WorkspaceView target)
    {
        if (_dialogOpen
            || SettingsFrame.Visibility == Visibility.Visible
            || !target.AcceptsApplicationInput)
        {
            return false;
        }
        var focused = RootGrid.XamlRoot is null
            ? null
            : FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        return !IsTextEntry(focused);
    }

    private bool TryHandleShortcut(
        WorkspaceView target,
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status,
        out bool matched)
    {
        matched = false;
        var match = ShortcutDefinitions.Match(
            new((int)key, ShortcutKeyState.CurrentModifiers(), ShortcutKeyState.IsAltGr()),
            target.ShortcutContext);
        if (match is null)
        {
            return false;
        }
        matched = true;
        var identity = (key, status.ScanCode, status.IsExtendedKey);
        if (status.WasKeyDown)
        {
            return _applicationKeysDown.Contains(identity);
        }
        _applicationKeysDown.Remove(identity);
        var handled = match.Value.Owner == ShortcutOwner.MainWindow
            ? HandleMainWindowShortcut(match.Value)
            : target.HandleShortcut(match.Value, repeat: false);
        if (handled)
        {
            _applicationKeysDown.Add(identity);
        }
        return handled;
    }

    private bool HandleMainWindowShortcut(ShortcutMatch match)
    {
        var entry = Nav.SelectedItem is NavigationViewItem { Tag: WorkspaceEntry selected }
            ? selected
            : _visibleWorkspace is null
                ? null
                : _workspaces.Values.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.View, _visibleWorkspace));
        switch (match.Action)
        {
            case ShortcutAction.FocusWorkspaceSearch:
                Nav.IsPaneOpen = true;
                NavSearch.Focus(FocusState.Keyboard);
                return true;
            case ShortcutAction.OpenSettings:
                Nav.SelectedItem = Nav.SettingsItem;
                return true;
            case ShortcutAction.NewWorkspace:
                CreateWorkspace();
                return true;
            case ShortcutAction.PreviousWorkspace:
                return SelectWorkspaceOffset(-1);
            case ShortcutAction.NextWorkspace:
                return SelectWorkspaceOffset(1);
            case ShortcutAction.SelectWorkspace:
                return SelectWorkspaceIndex(match.Index);
            case ShortcutAction.MoveWorkspaceUp when entry is not null:
                MoveWorkspace(entry, -1);
                return true;
            case ShortcutAction.MoveWorkspaceDown when entry is not null:
                MoveWorkspace(entry, 1);
                return true;
            case ShortcutAction.RenameWorkspace when entry is not null:
                _ = RenameWorkspaceAsync(entry);
                return true;
            case ShortcutAction.CloseWorkspace when entry is not null:
                CloseWorkspace(entry);
                return true;
            default:
                return false;
        }
    }

    private IntPtr HandleLowLevelMouse(
        int code,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (code >= 0
            && unchecked((uint)wParam.ToInt64()) == WmLeftButtonDown
            && !_closed
            && _windowActivated
            && _windowHandle != IntPtr.Zero
            && GetForegroundWindow() == _windowHandle)
        {
            var input = Marshal.PtrToStructure<LowLevelMouseInput>(lParam);
            BeginNativeWorkspacePointer(input.Point);
        }

        return CallNextHookEx(_mouseHookHandle, code, wParam, lParam);
    }

    private void BeginNativeWorkspacePointer(NativePoint screenPoint)
    {
        if (_closed || _windowHandle == IntPtr.Zero || RootGrid.XamlRoot is null)
        {
            return;
        }

        if (!ScreenToClient(_windowHandle, ref screenPoint))
        {
            return;
        }

        var scale = RootGrid.XamlRoot.RasterizationScale;
        var rootPoint = new Point(screenPoint.X / scale, screenPoint.Y / scale);
        var entry = WorkspaceEntryAt(rootPoint);
        if (entry is not null)
        {
            BeginWorkspacePointer(entry);
            return;
        }
        if (BeginNativePanePointer(rootPoint))
        {
            return;
        }

        _workspacePointerDown = false;
        _inputBridgeTimer.Stop();
        ClearBridgeCharacterKey();
        _workspaceInputBridgeActive = false;
        _nativePaneInputTarget = null;
    }

    private bool BeginNativePanePointer(Point rootPoint)
    {
        if (SettingsFrame.Visibility == Visibility.Visible
            || WorkspaceHost.Visibility != Visibility.Visible
            || _visibleWorkspace is not { } view
            || !view.IsLoaded)
        {
            return false;
        }

        try
        {
            var point = RootGrid.TransformToVisual(view)
                .TransformPoint(rootPoint);
            if (!view.ActivatePaneAt(point))
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        EnsureInputSiteSubclass();
        _workspacePointerDown = false;
        ClearBridgeCharacterKey();
        _workspaceInputTarget = view;
        _workspaceInputBridgeActive = true;
        _nativePaneInputTarget = view.SelectedTerminal;
        RestartInputBridgeTimer();
        return true;
    }

    private WorkspaceEntry? WorkspaceEntryAt(Point rootPoint)
    {
        foreach (var entry in _workspaces.Values)
        {
            var item = entry.Item;
            if (!item.IsLoaded
                || item.Visibility != Visibility.Visible
                || item.ActualWidth <= 0
                || item.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var topLeft = item.TransformToVisual(RootGrid)
                    .TransformPoint(new Point(0, 0));
                var bounds = new Rect(
                    topLeft.X,
                    topLeft.Y,
                    item.ActualWidth,
                    item.ActualHeight);
                if (bounds.Contains(rootPoint))
                {
                    return entry;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
        return null;
    }

    private void BeginWorkspacePointer(WorkspaceEntry entry)
    {
        EnsureInputSiteSubclass();
        _inputBridgeTimer.Stop();
        ClearBridgeCharacterKey();
        _nativePaneInputTarget = null;
        _workspacePointerDown = true;
        _workspaceInputTarget = entry.View;
        _workspaceInputBridgeActive = true;
        if (!ReferenceEquals(Nav.SelectedItem, entry.Item))
        {
            Nav.SelectedItem = entry.Item;
        }
        if (!ReferenceEquals(_visibleWorkspace, entry.View))
        {
            ShowWorkspace(entry);
        }
    }

    private static Windows.UI.Core.CorePhysicalKeyStatus PhysicalKeyStatusOf(IntPtr lParam)
    {
        var value = unchecked((ulong)lParam.ToInt64());
        return new Windows.UI.Core.CorePhysicalKeyStatus
        {
            RepeatCount = (uint)(value & 0xFFFF),
            ScanCode = (uint)((value >> 16) & 0xFF),
            IsExtendedKey = ((value >> 24) & 1) != 0,
            IsMenuKeyDown = ((value >> 29) & 1) != 0,
            WasKeyDown = ((value >> 30) & 1) != 0,
            IsKeyReleased = ((value >> 31) & 1) != 0,
        };
    }

    private static bool KeyCanProduceCharacter(VirtualKey key) =>
        MapVirtualKey((uint)key, 2) != 0
        || key is VirtualKey.Enter or VirtualKey.Back or VirtualKey.Tab or VirtualKey.Escape;

    private void OnRootGridLoaded(object sender, RoutedEventArgs args)
    {
        EnsureInputSiteSubclass();
        EnsureMouseHook();
        DispatcherQueue.TryEnqueue(() =>
        {
            EnsureInputSiteSubclass();
            EnsureMouseHook();
        });
    }

    private void EnsureInputSiteSubclass()
    {
        if (_closed || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var candidate = GetFocus();
        if (!IsInputSiteWindow(candidate)
            || !IsChild(_windowHandle, candidate))
        {
            candidate = FindInputSiteWindow(_windowHandle);
        }
        if (candidate == IntPtr.Zero || candidate == _inputSiteHandle)
        {
            return;
        }

        if (!SetWindowSubclass(
                candidate,
                _windowSubclassProc,
                new UIntPtr(WindowSubclassId),
                UIntPtr.Zero))
        {
            Diag.Log($"window input subclass failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        var previous = _inputSiteHandle;
        _inputSiteHandle = candidate;
        if (previous != IntPtr.Zero)
        {
            RemoveWindowSubclass(
                previous,
                _windowSubclassProc,
                new UIntPtr(WindowSubclassId));
        }
        Diag.Log("window input subclass installed");
    }

    private void EnsureMouseHook()
    {
        if (_closed
            || !_windowActivated
            || _windowHandle == IntPtr.Zero
            || _mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookHandle = SetWindowsHookEx(
            WhMouseLl,
            _lowLevelMouseProc,
            GetModuleHandle(null),
            0);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            Diag.Log($"mouse input hook failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void RemoveMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        if (UnhookWindowsHookEx(_mouseHookHandle))
        {
            _mouseHookHandle = IntPtr.Zero;
        }
        else
        {
            Diag.Log($"mouse input unhook failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr FindInputSiteWindow(IntPtr parent) =>
        FindChildWindow(parent, "InputSiteWindowClass");

    private static IntPtr FindChildWindow(IntPtr parent, string expectedClass)
    {
        var match = IntPtr.Zero;
        EnumChildWindowProc callback = (window, _) =>
        {
            if (!HasWindowClass(window, expectedClass))
            {
                return true;
            }

            match = window;
            return false;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return match;
    }

    private static bool IsInputSiteWindow(IntPtr window) =>
        HasWindowClass(window, "InputSiteWindowClass");

    private static bool HasWindowClass(IntPtr window, string expectedClass)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(64);
        return GetClassName(window, className, className.Capacity) > 0
            && string.Equals(
                className.ToString(),
                expectedClass,
                StringComparison.Ordinal);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            _windowActivated = false;
            ClearBridgeCharacterKey();
            RemoveMouseHook();
            return;
        }
        _windowActivated = true;
        EnsureInputSiteSubclass();
        EnsureMouseHook();
        DispatcherQueue.TryEnqueue(() =>
        {
            EnsureInputSiteSubclass();
            EnsureMouseHook();
        });
        FocusSelectedTerminal("window-activated");
    }

    private void FocusSelectedTerminal(string reason)
    {
        if (_windowActivated
            && SettingsFrame.Visibility != Visibility.Visible
            && _visibleWorkspace is { } view)
        {
            view.FocusSelectedTerminal(reason);
        }
    }

    private void ClearBridgeCharacterKey()
    {
        _bridgeCharacterKey = null;
        _applicationKeysDown.Clear();
    }

    private void RestartInputBridgeTimer()
    {
        _inputBridgeDeadline = Environment.TickCount64
            + (long)_inputBridgeTimer.Interval.TotalMilliseconds;
        _inputBridgeTimer.Stop();
        _inputBridgeTimer.Start();
    }

    private void OnInputBridgeTimerTick(object? sender, object args)
    {
        if (_workspaceInputBridgeActive
            && Environment.TickCount64 < _inputBridgeDeadline)
        {
            return;
        }

        _inputBridgeTimer.Stop();
        ClearBridgeCharacterKey();
        _nativePaneInputTarget = null;
        if (!_closed
            && !_workspacePointerDown
            && _workspaceInputTarget is { } target
            && ReferenceEquals(_visibleWorkspace, target))
        {
            _workspaceInputBridgeActive = false;
        }
    }

    private void OnNavSearchChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text.Trim();
        foreach (var entry in _workspaces.Values)
        {
            var subtitle = entry.Item.Content is StackPanel panel
                && panel.Children.Count > 1
                && panel.Children[1] is TextBlock text
                    ? text.Text
                    : string.Empty;
            var visibility = query.Length == 0
                || entry.Workspace.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (entry.Item.Visibility != visibility)
            {
                entry.Item.Visibility = visibility;
            }
        }
    }

    private void Relocalize()
    {
        NavSearch.PlaceholderText = Loc.S("Nav_Search");
        WorkspacesHeader.Content = Loc.S("Nav_Workspaces");
        var newWorkspace = Loc.S("Workspace_New");
        var closeWorkspace = Loc.S("Workspace_Close");
        ToolTipService.SetToolTip(NewWorkspaceButton, newWorkspace);
        ToolTipService.SetToolTip(CloseWorkspaceButton, closeWorkspace);
        AutomationProperties.SetName(NewWorkspaceButton, newWorkspace);
        AutomationProperties.SetName(CloseWorkspaceButton, closeWorkspace);
        foreach (var entry in _workspaces.Values)
        {
            entry.View.Relocalize();
            if (entry.Item.ContextFlyout is MenuFlyout { Items.Count: >= 5 } menu)
            {
                ((MenuFlyoutItem)menu.Items[0]).Text = Loc.S("Workspace_Rename");
                ((MenuFlyoutItem)menu.Items[1]).Text = Loc.S("Workspace_MoveUp");
                ((MenuFlyoutItem)menu.Items[2]).Text = Loc.S("Workspace_MoveDown");
                ((MenuFlyoutItem)menu.Items[4]).Text = closeWorkspace;
            }
        }
    }

    private void ApplyAppearance()
    {
        var settings = AppSettings.Current;
        _mux.ApplyTerminalAppearance();

        var hasCustomBackground = !string.IsNullOrWhiteSpace(settings.AppBackgroundColor)
            || (!string.IsNullOrWhiteSpace(settings.AppImagePath)
                && System.IO.File.Exists(settings.AppImagePath));

        SystemBackdrop = hasCustomBackground
            ? null
            : settings.Backdrop switch
            {
                BackdropKind.Mica => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                BackdropKind.Acrylic => new DesktopAcrylicBackdrop(),
                _ => null,
            };

        RootGrid.Background = ColorUtil.Parse(settings.AppBackgroundColor) is { } bg
            ? new SolidColorBrush(bg)
            : null;

        AppBackgroundImage.Opacity = settings.AppImageOpacity;
        AppBackgroundImage.Source = null;
        var hasImage = !string.IsNullOrWhiteSpace(settings.AppImagePath)
            && System.IO.File.Exists(settings.AppImagePath);
        if (hasImage)
        {
            try
            {
                AppBackgroundImage.Source =
                    new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(settings.AppImagePath));
            }
            catch (Exception ex)
            {
                Diag.Log($"app background image failed: {ex.Message}");
            }
        }

        AppBackgroundMask.Fill = hasImage
            ? new SolidColorBrush(ColorUtil.WithOpacity(
                ColorUtil.ParseOr(settings.AppMaskColor, Microsoft.UI.Colors.Black),
                settings.AppMaskOpacity))
            : null;

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.AppTheme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        App.ApplyAccentColor(ColorUtil.Parse(settings.AccentColor));
    }

    private void RestoreWorkspaces()
    {
        var workspaces = _mux.Workspaces();
        _tabCounter = workspaces
            .Select(workspace => WorkspaceNumber(workspace.Name))
            .DefaultIfEmpty()
            .Max();

        if (!string.IsNullOrWhiteSpace(_launchFolder))
        {
            var workspace = _mux.CreateWorkspace(NextWorkspaceTitle());
            if (!_mux.CreateTerminal(workspace.PublicId, _launchFolder))
            {
                throw new InvalidOperationException("The Explorer workspace terminal could not be created.");
            }
            workspaces = _mux.Workspaces();
        }
        else if (workspaces.Count == 0)
        {
            var workspace = _mux.CreateWorkspace(NextWorkspaceTitle());
            if (!_mux.CreateTerminal(workspace.PublicId))
            {
                throw new InvalidOperationException("The initial workspace terminal could not be created.");
            }
            workspaces = _mux.Workspaces();
        }

        var snapshot = _mux.Snapshot();
        RememberSnapshotCursor(snapshot);
        WorkspaceEntry? selected = null;
        foreach (var workspace in workspaces)
        {
            var entry = AddWorkspace(workspace, snapshot);
            if (workspace.Active)
            {
                selected = entry;
            }
        }
        selected ??= _workspaces.Values.FirstOrDefault();
        if (selected is not null)
        {
            Nav.SelectedItem = selected.Item;
            ShowWorkspace(selected);
        }
    }

    public void HandleActivation(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var workspace = _mux.CreateWorkspace(NextWorkspaceTitle());
            if (_mux.CreateTerminal(workspace.PublicId, folder))
            {
                workspace = _mux.Workspaces()
                    .Single(candidate => candidate.PublicId == workspace.PublicId);
                var entry = AddWorkspace(workspace, _mux.Snapshot());
                Nav.SelectedItem = entry.Item;
                ShowWorkspace(entry);
            }
        }
        Activate();
    }

    private WorkspaceEntry AddWorkspace(
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var view = new WorkspaceView(_mux, workspace);
        view.MainWindowShortcutRequested += match => HandleMainWindowShortcut(match);
        view.Render(snapshot);
        view.SetHostActive(false);
        view.Loaded += (_, _) =>
        {
            if (ReferenceEquals(_visibleWorkspace, view))
            {
                FocusSelectedTerminal("workspace-loaded");
            }
        };
        view.SelectedTerminalFocused += () =>
        {
            if (_workspaceInputBridgeActive
                && !_workspacePointerDown
                && ReferenceEquals(_visibleWorkspace, view)
                && ReferenceEquals(_workspaceInputTarget, view))
            {
                RestartInputBridgeTimer();
            }
        };
        _workspaceViewsHost.Children.Add(view);

        var item = BuildSessionItem(workspace, snapshot);
        var entry = new WorkspaceEntry
        {
            Workspace = workspace,
            View = view,
            Item = item,
        };
        item.Tag = entry;

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Loc.S("Workspace_Rename") };
        rename.Click += async (_, _) => await RenameWorkspaceAsync(entry);
        var moveUp = new MenuFlyoutItem { Text = Loc.S("Workspace_MoveUp") };
        moveUp.Click += (_, _) => MoveWorkspace(entry, -1);
        var moveDown = new MenuFlyoutItem { Text = Loc.S("Workspace_MoveDown") };
        moveDown.Click += (_, _) => MoveWorkspace(entry, 1);
        var close = new MenuFlyoutItem { Text = Loc.S("Workspace_Close") };
        close.Click += (_, _) => CloseWorkspace(entry);
        menu.Items.Add(rename);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(close);
        item.ContextFlyout = menu;

        _workspaces.Add(workspace.PublicId, entry);
        Nav.MenuItems.Add(item);
        return entry;
    }

    private static NavigationViewItem BuildSessionItem(
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var item = new NavigationViewItem
        {
            Icon = new SymbolIcon(Symbol.Document),
        };
        UpdateSessionItem(item, workspace, snapshot);
        return item;
    }

    private static void UpdateSessionItem(
        NavigationViewItem item,
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var subtitle = WorkspaceSubtitle(workspace, snapshot);
        if (item.Content is StackPanel { Children.Count: >= 2 } current
            && current.Children[0] is TextBlock name
            && current.Children[1] is TextBlock detail)
        {
            if (name.Text != workspace.Name)
            {
                name.Text = workspace.Name;
            }
            if (detail.Text != subtitle)
            {
                detail.Text = subtitle;
            }
            return;
        }

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = workspace.Name });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        item.Content = text;
    }

    private static string WorkspaceSubtitle(
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var screen = snapshot.Screens
            .Where(candidate => candidate.WorkspaceId == workspace.PublicId)
            .OrderByDescending(candidate => candidate.Focused)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        var activePane = screen?.Layout.ActivePaneId;
        var tab = snapshot.Tabs.FirstOrDefault(candidate =>
            candidate.PaneId == activePane && candidate.Focused);
        var cwd = tab is null
            ? null
            : snapshot.Terminals.FirstOrDefault(terminal => terminal.Id == tab.ContentId)?.Cwd;
        return !string.IsNullOrWhiteSpace(cwd)
            ? cwd
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string NextWorkspaceTitle() => $"PowerShell {++_tabCounter}";

    private static int WorkspaceNumber(string name)
    {
        const string prefix = "PowerShell ";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name[prefix.Length..], out var number)
                ? number
                : 0;
    }

    private void OnAddWorkspace(object sender, RoutedEventArgs args) => CreateWorkspace();

    private void CreateWorkspace()
    {
        var workspace = _mux.CreateWorkspace(NextWorkspaceTitle());
        if (!_mux.CreateTerminal(workspace.PublicId))
        {
            Diag.Log($"workspace terminal creation failed: {workspace.PublicId}");
            return;
        }
        workspace = _mux.Workspaces().Single(candidate => candidate.Id == workspace.Id);
        var entry = AddWorkspace(workspace, _mux.Snapshot());
        Nav.SelectedItem = entry.Item;
        ShowWorkspace(entry);
    }

    private bool SelectWorkspaceOffset(int delta)
    {
        var ordered = _mux.Workspaces();
        var current = ordered.ToList().FindIndex(workspace => workspace.Active);
        if (ordered.Count == 0 || current < 0)
        {
            return false;
        }
        return SelectWorkspaceIndex((current + delta + ordered.Count) % ordered.Count);
    }

    private bool SelectWorkspaceIndex(int index)
    {
        var ordered = _mux.Workspaces();
        if (index < 0 || index >= ordered.Count
            || !_workspaces.TryGetValue(ordered[index].PublicId, out var entry))
        {
            return false;
        }
        Nav.SelectedItem = entry.Item;
        ShowWorkspace(entry);
        return true;
    }

    private void OnCloseWorkspace(object sender, RoutedEventArgs args)
    {
        if (Nav.SelectedItem is NavigationViewItem { Tag: WorkspaceEntry entry })
        {
            CloseWorkspace(entry);
        }
    }

    private async System.Threading.Tasks.Task RenameWorkspaceAsync(WorkspaceEntry entry)
    {
        var name = new TextBox
        {
            Text = entry.Workspace.Name,
            SelectionStart = 0,
            SelectionLength = entry.Workspace.Name.Length,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Loc.S("Workspace_Rename"),
            Content = name,
            PrimaryButtonText = Loc.S("Action_Save"),
            CloseButtonText = Loc.S("Action_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !_mux.RenameWorkspace(entry.Workspace.PublicId, name.Text))
            {
                return;
            }
            SyncWorkspaceNavigation();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void MoveWorkspace(WorkspaceEntry entry, int delta)
    {
        var ordered = _mux.Workspaces().ToList();
        var index = ordered.FindIndex(workspace => workspace.PublicId == entry.Workspace.PublicId);
        if (index < 0)
        {
            return;
        }
        var destination = Math.Clamp(index + delta, 0, ordered.Count - 1);
        if (destination == index || !_mux.MoveWorkspace(entry.Workspace.PublicId, destination))
        {
            return;
        }
        SyncWorkspaceNavigation();
    }

    private void SyncWorkspaceNavigation()
    {
        var latest = _mux.Workspaces();
        var snapshot = _mux.Snapshot();
        SyncWorkspaceNavigation(latest, snapshot, followActive: false);
        RememberSnapshotCursor(snapshot);
    }

    private void SyncWorkspaceNavigation(
        IReadOnlyList<MuxRuntime.WorkspaceInfo> latest,
        MuxSnapshot snapshot,
        bool followActive)
    {
        var liveIds = latest.Select(workspace => workspace.PublicId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _workspaces.Values
                     .Where(entry => !liveIds.Contains(entry.Workspace.PublicId))
                     .ToList())
        {
            if (ReferenceEquals(_visibleWorkspace, stale.View))
            {
                _visibleWorkspace = null;
            }
            if (ReferenceEquals(_workspaceInputTarget, stale.View))
            {
                ClearBridgeCharacterKey();
                _workspaceInputTarget = null;
                _workspaceInputBridgeActive = false;
                _nativePaneInputTarget = null;
            }
            _workspaceViewsHost.Children.Remove(stale.View);
            stale.View.Dispose();
            _workspaces.Remove(stale.Workspace.PublicId);
            Nav.MenuItems.Remove(stale.Item);
        }

        foreach (var workspace in latest)
        {
            if (!_workspaces.TryGetValue(workspace.PublicId, out var entry))
            {
                entry = AddWorkspace(workspace, snapshot);
            }
            entry.Workspace = workspace;
            UpdateSessionItem(entry.Item, workspace, snapshot);
        }

        for (var index = 0; index < latest.Count; index++)
        {
            var item = _workspaces[latest[index].PublicId].Item;
            var destination = index + 1;
            if (Nav.MenuItems.IndexOf(item) == destination)
            {
                continue;
            }
            Nav.MenuItems.Remove(item);
            Nav.MenuItems.Insert(destination, item);
        }

        if (latest.Count == 0)
        {
            _inputBridgeTimer.Stop();
            ClearBridgeCharacterKey();
            _visibleWorkspace = null;
            _workspaceInputTarget = null;
            _workspaceInputBridgeActive = false;
            _nativePaneInputTarget = null;
            _workspacePointerDown = false;
            _workspaceViewsHost.Children.Clear();
            Close();
            return;
        }

        if (SettingsFrame.Visibility != Visibility.Visible)
        {
            var selected = Nav.SelectedItem as NavigationViewItem;
            if (followActive)
            {
                var active = latest.FirstOrDefault(workspace => workspace.Active);
                if (!string.IsNullOrEmpty(active.PublicId)
                    && _workspaces.TryGetValue(active.PublicId, out var activeEntry))
                {
                    selected = activeEntry.Item;
                }
            }
            if (selected?.Tag is not WorkspaceEntry selectedEntry
                || !_workspaces.ContainsKey(selectedEntry.Workspace.PublicId))
            {
                selected = latest.Count > 0 ? _workspaces[latest[0].PublicId].Item : null;
            }
            Nav.SelectedItem = selected;
            if (selected?.Tag is WorkspaceEntry entry
                && !ReferenceEquals(_visibleWorkspace, entry.View))
            {
                ShowWorkspace(entry);
            }
        }
        OnNavSearchChanged(NavSearch, null!);
    }

    private void CloseWorkspace(WorkspaceEntry entry)
    {
        var navigationIndex = Nav.MenuItems.IndexOf(entry.Item);
        if (!_mux.CloseWorkspace(entry.Workspace.PublicId))
        {
            Diag.Log($"workspace close failed: {entry.Workspace.PublicId}");
            return;
        }

        var wasVisible = ReferenceEquals(_visibleWorkspace, entry.View);
        if (wasVisible)
        {
            _visibleWorkspace = null;
        }
        if (ReferenceEquals(_workspaceInputTarget, entry.View))
        {
            _inputBridgeTimer.Stop();
            ClearBridgeCharacterKey();
            _workspaceInputTarget = null;
            _workspaceInputBridgeActive = false;
            _nativePaneInputTarget = null;
        }
        _workspaceViewsHost.Children.Remove(entry.View);
        entry.View.Dispose();
        _workspaces.Remove(entry.Workspace.PublicId);
        Nav.MenuItems.Remove(entry.Item);

        if (_workspaces.Count == 0)
        {
            Close();
            return;
        }
        if (wasVisible)
        {
            var nextIndex = Math.Clamp(navigationIndex, 1, Nav.MenuItems.Count - 1);
            var next = (Nav.MenuItems[nextIndex] as NavigationViewItem)?.Tag as WorkspaceEntry
                ?? _workspaces.Values.First();
            Nav.SelectedItem = next.Item;
            ShowWorkspace(next);
        }
    }

    private void OnNavSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            _inputBridgeTimer.Stop();
            ClearBridgeCharacterKey();
            _workspaceInputTarget = null;
            _workspaceInputBridgeActive = false;
            _nativePaneInputTarget = null;
            _workspacePointerDown = false;
            SettingsFrame.Navigate(typeof(Views.SettingsPage));
            SettingsFrame.Visibility = Visibility.Visible;
            WorkspaceHost.Visibility = Visibility.Collapsed;
            WorkspaceHost.IsHitTestVisible = false;
            CloseWorkspaceButton.IsEnabled = false;
            return;
        }

        SettingsFrame.Visibility = Visibility.Collapsed;
        WorkspaceHost.Visibility = Visibility.Visible;
        WorkspaceHost.IsHitTestVisible = true;
        CloseWorkspaceButton.IsEnabled = true;
        if (args.SelectedItem is NavigationViewItem { Tag: WorkspaceEntry entry })
        {
            ShowWorkspace(entry);
        }
    }

    private static WorkspaceEntry? WorkspaceEntryFrom(DependencyObject? source)
    {
        while (source is not null && source is not NavigationViewItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return (source as NavigationViewItem)?.Tag as WorkspaceEntry;
    }

    private static TerminalView? TerminalViewFrom(DependencyObject? source)
    {
        while (source is not null && source is not TerminalView)
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source as TerminalView;
    }

    private WorkspaceView? WorkspaceInputTargetFor(DependencyObject? source)
    {
        if (!_workspaceInputBridgeActive
            || _workspaceInputTarget is not { } target
            || !ReferenceEquals(_visibleWorkspace, target))
        {
            return null;
        }

        var entry = WorkspaceEntryFrom(source);
        if (entry is not null
            && ReferenceEquals(Nav.SelectedItem, entry.Item)
            && ReferenceEquals(target, entry.View))
        {
            return target;
        }

        var sourceTerminal = TerminalViewFrom(source);
        return sourceTerminal is not null && !target.IsSelectedTerminal(sourceTerminal)
            ? target
            : null;
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsTextEntry(args.OriginalSource as DependencyObject))
        {
            return;
        }
        if (_visibleWorkspace is { } target
            && AcceptsApplicationInput(target)
            && TryHandleShortcut(target, args.Key, args.KeyStatus, out _))
        {
            args.Handled = true;
            return;
        }
        WorkspaceInputTargetFor(args.OriginalSource as DependencyObject)?.ForwardKeyDown(args);
    }

    private void OnInputKeyUp(object sender, KeyRoutedEventArgs args)
    {
        var identity = (args.Key, args.KeyStatus.ScanCode, args.KeyStatus.IsExtendedKey);
        if (_applicationKeysDown.Remove(identity))
        {
            args.Handled = true;
            return;
        }
        WorkspaceInputTargetFor(args.OriginalSource as DependencyObject)?.ForwardKeyUp(args);
    }

    private static bool IsTextEntry(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox or AutoSuggestBox)
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnInputCharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs args) =>
        WorkspaceInputTargetFor(args.OriginalSource as DependencyObject)?.ForwardCharacterReceived(args);

    private void OnNavPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var entry = WorkspaceEntryFrom(args.OriginalSource as DependencyObject);
        if (entry is null
            || !args.GetCurrentPoint(Nav).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginWorkspacePointer(entry);
    }

    private void OnNavPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _workspacePointerDown = false;
        EnsureInputSiteSubclass();
        var entry = WorkspaceEntryFrom(args.OriginalSource as DependencyObject);
        if (entry is null || !ReferenceEquals(Nav.SelectedItem, entry.Item))
        {
            return;
        }

        _nativePaneInputTarget = null;
        _workspaceInputTarget = entry.View;
        _workspaceInputBridgeActive = true;
        FocusSelectedTerminal("workspace-pointer-released");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed
                && ReferenceEquals(Nav.SelectedItem, entry.Item)
                && ReferenceEquals(_visibleWorkspace, entry.View))
            {
                FocusSelectedTerminal("workspace-pointer-settled");
            }
        });
    }

    private void ShowWorkspace(WorkspaceEntry entry)
    {
        ClearBridgeCharacterKey();
        _nativePaneInputTarget = null;
        if (!_mux.SelectWorkspace(entry.Workspace.PublicId))
        {
            Diag.Log($"workspace selection failed: {entry.Workspace.PublicId}");
            return;
        }
        var switchingWorkspace = !ReferenceEquals(_visibleWorkspace, entry.View);
        _workspaceInputTarget = entry.View;
        if (switchingWorkspace)
        {
            _workspaceInputBridgeActive = true;
        }
        try
        {
            entry.View.Render(_mux.Snapshot());
        }
        catch (Exception ex)
        {
            Diag.Log($"workspace render failed: {ex.Message}");
        }
        if (!ReferenceEquals(_visibleWorkspace, entry.View))
        {
            _visibleWorkspace?.SetHostActive(false);
            _visibleWorkspace = entry.View;
            entry.View.SetHostActive(true);
        }
        FocusSelectedTerminal("workspace-selected");
    }

    private void OnTopologyTick(object? sender, object args)
    {
        if (_closed || _topologyPolling)
        {
            return;
        }
        _topologyPolling = true;
        try
        {
            var latest = _mux.Workspaces();
            var snapshot = _mux.Snapshot();
            var changed = snapshot.Cursor.Generation != _snapshotGeneration
                || snapshot.Cursor.Revision != _snapshotRevision;
            SyncWorkspaceNavigation(latest, snapshot, followActive: changed);
            if (_visibleWorkspace is { } view)
            {
                if (changed)
                {
                    view.Render(snapshot);
                    FocusSelectedTerminal("topology-rendered");
                }
                else
                {
                    view.UpdateStatus(snapshot);
                }
            }
            RememberSnapshotCursor(snapshot);
            _topologyFailureLogged = false;
        }
        catch (Exception ex)
        {
            if (!_topologyFailureLogged)
            {
                Diag.Log($"topology polling failed: {ex.Message}");
                _topologyFailureLogged = true;
            }
        }
        finally
        {
            _topologyPolling = false;
        }
    }

    private void RememberSnapshotCursor(MuxSnapshot snapshot)
    {
        _snapshotGeneration = snapshot.Cursor.Generation;
        _snapshotRevision = snapshot.Cursor.Revision;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        AppSettings.Changed -= ApplyAppearance;
        AppSettings.Changed -= Relocalize;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
        RootGrid.Loaded -= OnRootGridLoaded;
        if (_inputSiteHandle != IntPtr.Zero)
        {
            RemoveWindowSubclass(
                _inputSiteHandle,
                _windowSubclassProc,
                new UIntPtr(WindowSubclassId));
            _inputSiteHandle = IntPtr.Zero;
        }
        RemoveMouseHook();
        _windowHandle = IntPtr.Zero;
        NavSearch.TextChanged -= OnNavSearchChanged;
        _inputBridgeTimer.Stop();
        ClearBridgeCharacterKey();
        _inputBridgeTimer.Tick -= OnInputBridgeTimerTick;
        _topologyTimer.Stop();
        _topologyTimer.Tick -= OnTopologyTick;

        foreach (var entry in _workspaces.Values)
        {
            entry.View.Dispose();
        }
        _visibleWorkspace = null;
        ClearBridgeCharacterKey();
        _workspaceInputTarget = null;
        _workspaceInputBridgeActive = false;
        _nativePaneInputTarget = null;
        _workspacePointerDown = false;
        _workspaceViewsHost.Children.Clear();
        _workspaces.Clear();
        AppSettings.Current.Save();
        _mux.Dispose();
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        WindowSubclassProc callback,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window,
        WindowSubclassProc callback,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parent,
        EnumChildWindowProc callback,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
}
