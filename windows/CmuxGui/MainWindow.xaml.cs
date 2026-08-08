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
        public required MuxRuntime.WorkspaceInfo Workspace { get; init; }
        public required WorkspaceView View { get; init; }
        public required NavigationViewItem Item { get; init; }
    }

    private readonly MuxRuntime _mux;
    private readonly Dictionary<ulong, WorkspaceEntry> _workspaces = [];
    private int _tabCounter;
    private bool _windowActivated;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        _mux = MuxRuntime.Open();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Relocalize();
        ApplyAppearance();
        AppSettings.Changed += ApplyAppearance;
        AppSettings.Changed += Relocalize;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        RestoreWorkspaces();
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
    }

    private void ApplyAppearance()
    {
        var settings = AppSettings.Current;

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
    }

    private void RestoreWorkspaces()
    {
        var workspaces = _mux.Workspaces();
        _tabCounter = workspaces
            .Select(workspace => WorkspaceNumber(workspace.Name))
            .DefaultIfEmpty()
            .Max();

        if (!string.IsNullOrWhiteSpace(App.LaunchFolder))
        {
            var workspace = _mux.CreateWorkspace(NextWorkspaceTitle());
            if (!_mux.CreateTerminal(workspace.PublicId, App.LaunchFolder))
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
        var close = new MenuFlyoutItem { Text = Loc.S("Workspace_Close") };
        close.Click += (_, _) => CloseWorkspace(entry);
        menu.Items.Add(close);
        item.ContextFlyout = menu;

        _workspaces.Add(workspace.Id, entry);
        Nav.MenuItems.Add(item);
        return entry;
    }

    private static NavigationViewItem BuildSessionItem(
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = workspace.Name });
        text.Children.Add(new TextBlock
        {
            Text = WorkspaceSubtitle(workspace, snapshot),
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new NavigationViewItem
        {
            Content = text,
            Icon = new SymbolIcon(Symbol.Document),
        };
    }

    private static string WorkspaceSubtitle(
        MuxRuntime.WorkspaceInfo workspace,
        MuxSnapshot snapshot)
    {
        var screen = snapshot.Screens
            .Where(candidate => candidate.WorkspaceId == workspace.PublicId)
            .OrderBy(candidate => candidate.Index)
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

    private void CloseWorkspace(WorkspaceEntry entry)
    {
        if (!_mux.CloseWorkspace(entry.Workspace.Id))
        {
            Diag.Log($"workspace close failed: {entry.Workspace.Id}");
            return;
        }

        var wasVisible = ReferenceEquals(WorkspaceHost.Content, entry.View);
        entry.View.Dispose();
        _workspaces.Remove(entry.Workspace.Id);
        Nav.MenuItems.Remove(entry.Item);

        if (_workspaces.Count == 0)
        {
            Close();
            return;
        }
        if (wasVisible)
        {
            var next = _workspaces.Values.First();
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
        if (!_mux.SelectWorkspace(entry.Workspace.Id))
        {
            Diag.Log($"workspace selection failed: {entry.Workspace.Id}");
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

        foreach (var entry in _workspaces.Values)
        {
            entry.View.Dispose();
        }
        _workspaces.Clear();
        _mux.Dispose();
    }
}
