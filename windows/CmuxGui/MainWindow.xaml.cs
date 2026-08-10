using System;
using System.Collections.Generic;
using System.Linq;
using CmuxGui.Controls;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CmuxGui;

public sealed partial class MainWindow : Window
{
    private sealed class WorkspaceEntry
    {
        public required MuxRuntime.WorkspaceInfo Workspace { get; set; }
        public required WorkspaceView View { get; init; }
        public required NavigationViewItem Item { get; init; }
    }

    private readonly MuxRuntime _mux;
    private readonly Dictionary<string, WorkspaceEntry> _workspaces = [];
    private readonly string? _launchFolder;
    private readonly DispatcherTimer _topologyTimer = new();
    private string _snapshotGeneration = string.Empty;
    private string _snapshotRevision = string.Empty;
    private bool _topologyPolling;
    private bool _topologyFailureLogged;
    private int _tabCounter;
    private bool _windowActivated;
    private bool _closed;

    public MainWindow(string sessionName, string? launchFolder, bool persistentSession = true)
    {
        InitializeComponent();
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
        NavSearch.TextChanged += OnNavSearchChanged;
        _topologyTimer.Interval = TimeSpan.FromMilliseconds(750);
        _topologyTimer.Tick += OnTopologyTick;

        RestoreWorkspaces();
        _topologyTimer.Start();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }
        _windowActivated = true;
        FocusSelectedTerminal("window-activated");
    }

    private void FocusSelectedTerminal(string reason)
    {
        if (_windowActivated && WorkspaceHost.Content is WorkspaceView view)
        {
            view.FocusSelectedTerminal(reason);
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
        view.Render(snapshot);
        view.Loaded += (_, _) => FocusSelectedTerminal("workspace-loaded");

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

    private void OnAddWorkspace(object sender, RoutedEventArgs args)
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !_mux.RenameWorkspace(entry.Workspace.PublicId, name.Text))
        {
            return;
        }
        SyncWorkspaceNavigation();
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
            WorkspaceHost.Content = null;
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
                && !ReferenceEquals(WorkspaceHost.Content, entry.View))
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

        var wasVisible = ReferenceEquals(WorkspaceHost.Content, entry.View);
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
            SettingsFrame.Navigate(typeof(Views.SettingsPage));
            SettingsFrame.Visibility = Visibility.Visible;
            WorkspaceHost.Visibility = Visibility.Collapsed;
            CloseWorkspaceButton.IsEnabled = false;
            return;
        }

        SettingsFrame.Visibility = Visibility.Collapsed;
        WorkspaceHost.Visibility = Visibility.Visible;
        CloseWorkspaceButton.IsEnabled = true;
        if (args.SelectedItem is NavigationViewItem { Tag: WorkspaceEntry entry })
        {
            ShowWorkspace(entry);
        }
    }

    private void ShowWorkspace(WorkspaceEntry entry)
    {
        if (!_mux.SelectWorkspace(entry.Workspace.PublicId))
        {
            Diag.Log($"workspace selection failed: {entry.Workspace.PublicId}");
            return;
        }
        try
        {
            entry.View.Render(_mux.Snapshot());
        }
        catch (Exception ex)
        {
            Diag.Log($"workspace render failed: {ex.Message}");
        }
        WorkspaceHost.Content = entry.View;
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
            if (WorkspaceHost.Content is WorkspaceView view)
            {
                if (changed)
                {
                    view.Render(snapshot);
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
        NavSearch.TextChanged -= OnNavSearchChanged;
        _topologyTimer.Stop();
        _topologyTimer.Tick -= OnTopologyTick;

        foreach (var entry in _workspaces.Values)
        {
            entry.View.Dispose();
        }
        _workspaces.Clear();
        AppSettings.Current.Save();
        _mux.Dispose();
    }
}
