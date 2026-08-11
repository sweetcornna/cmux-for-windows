using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace CmuxGui.Controls;

internal sealed class WorkspaceView : UserControl, IDisposable
{
    private readonly MuxRuntime _mux;
    private readonly Dictionary<string, TerminalView> _terminals = [];
    private readonly Dictionary<string, BrowserView> _browsers = [];
    private readonly Dictionary<string, TabViewItem> _tabItems = [];
    private readonly Dictionary<string, Border> _paneBorders = [];
    private readonly HashSet<string> _renderedTerminals = [];
    private readonly HashSet<string> _renderedBrowsers = [];
    private bool _rendering;
    private bool _disposed;
    private bool _hostActive;
    private string? _activePaneId;
    private TerminalView? _selectedTerminal;
    private MuxSnapshot? _snapshot;
    private string _renderKey = string.Empty;

    public WorkspaceView(MuxRuntime mux, MuxRuntime.WorkspaceInfo workspace)
    {
        _mux = mux;
        Workspace = workspace;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AddAccelerator(VirtualKey.Left, "left");
        AddAccelerator(VirtualKey.Right, "right");
        AddAccelerator(VirtualKey.Up, "up");
        AddAccelerator(VirtualKey.Down, "down");

        var zoom = new KeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        zoom.Invoked += (_, args) =>
        {
            if (_activePaneId is not null)
            {
                Mutate(() => _mux.ZoomPane(_activePaneId), $"zoom pane {_activePaneId}");
                args.Handled = true;
            }
        };
        KeyboardAccelerators.Add(zoom);
    }

    private void AddAccelerator(VirtualKey key, string direction)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu,
        };
        accelerator.Invoked += (_, args) =>
        {
            if (_activePaneId is not null)
            {
                Mutate(
                    () => _mux.FocusPaneDirection(_activePaneId, direction),
                    $"focus {direction} from {_activePaneId}");
                args.Handled = true;
            }
        };
        KeyboardAccelerators.Add(accelerator);
    }

    public MuxRuntime.WorkspaceInfo Workspace { get; }

    public event Action? SelectedTerminalFocused;

    public bool IsSelectedTerminal(TerminalView terminal) =>
        ReferenceEquals(_selectedTerminal, terminal);

    internal TerminalView? SelectedTerminal => _selectedTerminal;

    internal bool ActivatePaneAt(Point point)
    {
        if (!_hostActive || !IsLoaded)
        {
            return false;
        }

        foreach (var (paneId, border) in _paneBorders)
        {
            if (!border.IsLoaded
                || border.Visibility != Visibility.Visible
                || border.ActualWidth <= 0
                || border.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var topLeft = border.TransformToVisual(this)
                    .TransformPoint(new Point(0, 0));
                var bounds = new Rect(
                    topLeft.X,
                    topLeft.Y,
                    border.ActualWidth,
                    border.ActualHeight);
                if (bounds.Contains(point)
                    && border.Child is TabView tabView)
                {
                    return ActivatePane(paneId, tabView);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
        return false;
    }

    public void SetHostActive(bool active)
    {
        if (_disposed)
        {
            return;
        }

        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (_hostActive == active)
        {
            return;
        }
        _hostActive = active;
        foreach (var terminal in _terminals.Values)
        {
            terminal.SetHostActive(active);
        }
    }

    public void Render(MuxSnapshot snapshot)
    {
        var renderKey = BuildRenderKey(snapshot);
        if (renderKey == _renderKey)
        {
            UpdateStatus(snapshot);
            return;
        }

        _rendering = true;
        try
        {
            _snapshot = snapshot;
            _renderedTerminals.Clear();
            _renderedBrowsers.Clear();
            foreach (var item in _tabItems.Values)
            {
                item.Content = null;
            }
            _tabItems.Clear();
            _paneBorders.Clear();
            _selectedTerminal = null;
            Content = null;

            var screens = snapshot.Screens
                .Where(screen => screen.WorkspaceId == Workspace.PublicId)
                .OrderBy(screen => screen.Index)
                .ToList();
            var screen = screens.FirstOrDefault(candidate => candidate.Focused)
                ?? screens.FirstOrDefault();
            if (screen is null)
            {
                _activePaneId = null;
                Content = BuildEmptyWorkspace();
                DisposeUnrenderedViews();
                _renderKey = renderKey;
                return;
            }

            var paneIds = snapshot.Panes
                .Where(pane => pane.ScreenId == screen.Id)
                .Select(pane => pane.Id)
                .ToHashSet(StringComparer.Ordinal);
            var tabs = snapshot.Tabs
                .Where(tab => paneIds.Contains(tab.PaneId))
                .ToDictionary(tab => tab.Id, StringComparer.Ordinal);
            var terminals = snapshot.Terminals
                .ToDictionary(terminal => terminal.Id, StringComparer.Ordinal);
            var browsers = snapshot.Browsers
                .ToDictionary(browser => browser.Id, StringComparer.Ordinal);
            _activePaneId = screen.Layout.ActivePaneId;

            var body = !string.IsNullOrWhiteSpace(screen.Layout.ZoomedPaneId)
                ? BuildPane(screen.Layout.ZoomedPaneId!, tabs, terminals, browsers)
                : BuildNode(screen.Layout.Root, tabs, terminals, browsers);
            Content = BuildScreenLayout(screens, screen, body);
            foreach (var tabId in _renderedTerminals)
            {
                _terminals[tabId].NotifyHostReparented();
            }
            DisposeUnrenderedViews();
            UpdatePaneFocus();
            _renderKey = renderKey;
        }
        finally
        {
            _rendering = false;
        }
    }

    private string BuildRenderKey(MuxSnapshot snapshot)
    {
        var screens = snapshot.Screens
            .Where(screen => screen.WorkspaceId == Workspace.PublicId)
            .OrderBy(screen => screen.Index)
            .ThenBy(screen => screen.Id)
            .ToList();
        var screenIds = screens.Select(screen => screen.Id).ToHashSet(StringComparer.Ordinal);
        var panes = snapshot.Panes
            .Where(pane => screenIds.Contains(pane.ScreenId))
            .OrderBy(pane => pane.Id)
            .ToList();
        var paneIds = panes.Select(pane => pane.Id).ToHashSet(StringComparer.Ordinal);
        var tabs = snapshot.Tabs
            .Where(tab => paneIds.Contains(tab.PaneId))
            .OrderBy(tab => tab.PaneId)
            .ThenBy(tab => tab.Index)
            .ThenBy(tab => tab.Id)
            .ToList();
        var contentIds = tabs.Select(tab => tab.ContentId).ToHashSet(StringComparer.Ordinal);

        return JsonSerializer.Serialize(new
        {
            Screens = screens.Select(screen => new
            {
                screen.Id,
                screen.WorkspaceId,
                screen.Name,
                screen.Index,
                screen.Focused,
                Layout = new
                {
                    screen.Layout.ZoomedPaneId,
                    screen.Layout.Root,
                },
            }),
            Panes = panes.Select(pane => new
            {
                pane.Id,
                pane.ScreenId,
                pane.Name,
            }),
            Tabs = tabs.Select(tab => new
            {
                tab.Id,
                tab.PaneId,
                tab.Name,
                tab.Index,
                tab.ContentKind,
                tab.ContentId,
            }),
            Terminals = snapshot.Terminals
                .Where(terminal => contentIds.Contains(terminal.Id))
                .Select(terminal => terminal.Id)
                .OrderBy(id => id),
            Browsers = snapshot.Browsers
                .Where(browser => contentIds.Contains(browser.Id))
                .Select(browser => new { browser.Id, browser.TabId, browser.Source })
                .OrderBy(browser => browser.Id),
        });
    }

    public bool ForwardKeyDown(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status) =>
        _hostActive
        && IsLoaded
        && _selectedTerminal?.ForwardKeyDown(key, status) == true;

    public bool ForwardKeyUp(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status) =>
        _hostActive
        && IsLoaded
        && _selectedTerminal?.ForwardKeyUp(key, status) == true;

    public bool ForwardCharacterReceived(uint keyCode) =>
        _hostActive
        && IsLoaded
        && _selectedTerminal?.ForwardCharacterReceived(keyCode) == true;

    public void ForwardKeyDown(KeyRoutedEventArgs args)
    {
        if (_hostActive && IsLoaded)
        {
            _selectedTerminal?.ForwardKeyDown(args);
        }
    }

    public void ForwardKeyUp(KeyRoutedEventArgs args)
    {
        if (_hostActive && IsLoaded)
        {
            _selectedTerminal?.ForwardKeyUp(args);
        }
    }

    public void ForwardCharacterReceived(CharacterReceivedRoutedEventArgs args)
    {
        if (_hostActive && IsLoaded)
        {
            _selectedTerminal?.ForwardCharacterReceived(args);
        }
    }

    public void FocusSelectedTerminal(string reason)
    {
        var terminal = _selectedTerminal;
        if (_disposed || !IsLoaded || terminal is null)
        {
            return;
        }

        terminal.FocusTerminal(reason);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && IsLoaded && ReferenceEquals(_selectedTerminal, terminal))
            {
                terminal.FocusTerminal($"{reason}-settled");
            }
        });
    }

    public void UpdateStatus(MuxSnapshot snapshot)
    {
        _snapshot = snapshot;
        var screen = snapshot.Screens
            .Where(screen => screen.WorkspaceId == Workspace.PublicId)
            .OrderBy(screen => screen.Index)
            .FirstOrDefault(screen => screen.Focused)
            ?? snapshot.Screens
                .Where(screen => screen.WorkspaceId == Workspace.PublicId)
                .OrderBy(screen => screen.Index)
                .FirstOrDefault();
        _activePaneId = screen?.Layout.ActivePaneId;
        var activeTab = _activePaneId is null
            ? null
            : snapshot.Tabs
                .Where(tab => tab.PaneId == _activePaneId)
                .OrderByDescending(tab => tab.Focused)
                .ThenBy(tab => tab.Index)
                .FirstOrDefault();
        _selectedTerminal = activeTab is not null
            && _tabItems.TryGetValue(activeTab.Id, out var activeItem)
                ? activeItem.Content as TerminalView
                : null;
        UpdatePaneFocus();

        var terminals = snapshot.Terminals
            .ToDictionary(terminal => terminal.Id, StringComparer.Ordinal);
        var browsers = snapshot.Browsers
            .ToDictionary(browser => browser.Id, StringComparer.Ordinal);
        foreach (var tab in snapshot.Tabs)
        {
            _browsers.TryGetValue(tab.Id, out var browserView);
            if (browserView is not null
                && browsers.TryGetValue(tab.ContentId, out var browserSnapshot))
            {
                browserView.Update(browserSnapshot);
            }
            if (_tabItems.TryGetValue(tab.Id, out var item))
            {
                item.Header = TabTitle(tab, terminals, browsers, browserView?.DocumentTitle);
            }
        }
    }

    private void UpdateBrowserTitle(string tabId, BrowserView browser)
    {
        if (_snapshot is null
            || !_tabItems.TryGetValue(tabId, out var item)
            || _snapshot.Tabs.FirstOrDefault(tab => tab.Id == tabId) is not { } tab)
        {
            return;
        }
        var terminals = _snapshot.Terminals
            .ToDictionary(terminal => terminal.Id, StringComparer.Ordinal);
        var browsers = _snapshot.Browsers
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        item.Header = TabTitle(tab, terminals, browsers, browser.DocumentTitle);
    }

    private FrameworkElement BuildScreenLayout(
        IReadOnlyList<ScreenSnapshot> screens,
        ScreenSnapshot selectedScreen,
        FrameworkElement body)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 0, 8, 4),
        };
        var selector = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ComboBoxItem? selected = null;
        foreach (var screen in screens)
        {
            var item = new ComboBoxItem
            {
                Content = ScreenTitle(screen),
                Tag = screen.Id,
            };
            selector.Items.Add(item);
            if (screen.Id == selectedScreen.Id)
            {
                selected = item;
            }
        }
        selector.SelectedItem = selected;
        selector.SelectionChanged += (_, _) =>
        {
            if (_rendering || selector.SelectedItem is not ComboBoxItem { Tag: string screenId })
            {
                return;
            }
            Mutate(() => _mux.FocusScreen(screenId), $"focus screen {screenId}");
        };
        header.Children.Add(selector);
        header.Children.Add(ActionButton(
            ActionGlyph("\uE710"),
            "Screen_New",
            () => Mutate(
                () => _mux.CreateScreen(Workspace.PublicId),
                $"create screen in {Workspace.PublicId}")));
        header.Children.Add(ActionButton(
            ActionGlyph("\uE8AC"),
            "Screen_Rename",
            () => _ = RenameScreenAsync(selectedScreen)));
        header.Children.Add(ActionButton(
            ActionGlyph("\uE74D"),
            "Screen_Close",
            () => Mutate(
                () => _mux.CloseScreen(selectedScreen.Id),
                $"close screen {selectedScreen.Id}")));
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private async System.Threading.Tasks.Task RenameScreenAsync(ScreenSnapshot screen)
    {
        var name = new TextBox { Text = screen.Name ?? string.Empty };
        name.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.S("Screen_Rename"),
            Content = name,
            PrimaryButtonText = Loc.S("Action_Save"),
            CloseButtonText = Loc.S("Action_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var value = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            Mutate(() => _mux.RenameScreen(screen.Id, value), $"rename screen {screen.Id}");
        }
    }

    private static string ScreenTitle(ScreenSnapshot screen) =>
        string.IsNullOrWhiteSpace(screen.Name)
            ? $"{Loc.S("Screen_Screen")} {screen.Index + 1}"
            : screen.Name;

    private FrameworkElement BuildNode(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers) => node.Kind switch
    {
        "leaf" when node.PaneId is not null => BuildPane(node.PaneId, tabs, terminals, browsers),
        "split" when node.First is not null && node.Second is not null =>
            BuildSplit(node, tabs, terminals, browsers),
        "viewport" => BuildViewport(node, tabs, terminals, browsers),
        "stack" => BuildStack(node, tabs, terminals, browsers),
        _ => BuildUnavailable(Loc.S("Pane_Empty")),
    };

    private FrameworkElement BuildSplit(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers)
    {
        var horizontal = node.Direction == "horizontal";
        var ratio = Math.Clamp(node.Ratio, 0.05, 0.95);
        var grid = new Grid();
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });
        }

        var first = BuildNode(node.First!, tabs, terminals, browsers);
        var second = BuildNode(node.Second!, tabs, terminals, browsers);
        var divider = BuildSplitDivider(grid, node, horizontal);
        grid.Children.Add(first);
        grid.Children.Add(divider);
        grid.Children.Add(second);
        if (horizontal)
        {
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            Grid.SetRow(divider, 1);
            Grid.SetRow(second, 2);
        }
        return grid;
    }

    private FrameworkElement BuildSplitDivider(Grid grid, LayoutNodeSnapshot node, bool horizontal)
    {
        var divider = new Grid();
        divider.Children.Add(new Border
        {
            Background = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
            Width = horizontal ? 1 : double.NaN,
            Height = horizontal ? double.NaN : 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var thumb = new Thumb
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
            thumb,
            $"SplitDivider_{node.SplitId}");
        divider.Children.Add(thumb);

        var ratio = node.Ratio;
        thumb.DragDelta += (_, args) =>
        {
            var extent = horizontal ? grid.ActualWidth : grid.ActualHeight;
            var change = horizontal ? args.HorizontalChange : args.VerticalChange;
            ratio = Math.Clamp(ratio + change / Math.Max(1, extent), 0.05, 0.95);
            if (horizontal)
            {
                grid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
                grid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
            }
            else
            {
                grid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
                grid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
            }
        };
        thumb.DragCompleted += (_, _) =>
        {
            var paneId = FirstPaneId(node.First!);
            if (paneId is not null && node.SplitId is not null)
            {
                Mutate(
                    () => _mux.SetSplitRatio(paneId, node.SplitId, ratio),
                    $"resize split {node.SplitId}");
            }
        };
        return divider;
    }

    private static string? FirstPaneId(LayoutNodeSnapshot node)
    {
        if (node.PaneId is not null)
        {
            return node.PaneId;
        }
        if (node.PaneIds.Count > 0)
        {
            return node.PaneIds[0];
        }
        foreach (var column in node.Columns)
        {
            if (FirstPaneId(column.Root) is { } pane)
            {
                return pane;
            }
        }
        return node.First is not null && FirstPaneId(node.First) is { } first
            ? first
            : node.Second is not null ? FirstPaneId(node.Second) : null;
    }

    private FrameworkElement BuildViewport(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers)
    {
        if (node.Columns.Count == 0)
        {
            return BuildUnavailable(Loc.S("Pane_Empty"));
        }
        var grid = new Grid();
        for (var index = 0; index < node.Columns.Count; index++)
        {
            var column = node.Columns[index];
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, column.Width), GridUnitType.Star),
            });
            var content = BuildNode(column.Root, tabs, terminals, browsers);
            Grid.SetColumn(content, index * 2);
            grid.Children.Add(content);
            if (index == node.Columns.Count - 1)
            {
                continue;
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            var divider = BuildViewportDivider(grid, node, index);
            Grid.SetColumn(divider, index * 2 + 1);
            grid.Children.Add(divider);
        }
        return grid;
    }

    private FrameworkElement BuildViewportDivider(Grid grid, LayoutNodeSnapshot node, int index)
    {
        var divider = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new Border
            {
                Background = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var dragging = false;
        var startX = 0d;
        var left = node.Columns[index].Width;
        var right = node.Columns[index + 1].Width;
        divider.PointerPressed += (_, args) =>
        {
            startX = args.GetCurrentPoint(grid).Position.X;
            dragging = divider.CapturePointer(args.Pointer);
            args.Handled = dragging;
        };
        divider.PointerMoved += (_, args) =>
        {
            if (!dragging)
            {
                return;
            }
            var current = args.GetCurrentPoint(grid).Position.X;
            var totalUnits = node.Columns.Sum(column => Math.Max(1, column.Width));
            var delta = (current - startX) / Math.Max(1, grid.ActualWidth) * totalUnits;
            var adjustedLeft = Math.Max(1, left + delta);
            var adjustedRight = Math.Max(1, right - delta);
            grid.ColumnDefinitions[index * 2].Width =
                new GridLength(adjustedLeft, GridUnitType.Star);
            grid.ColumnDefinitions[(index + 1) * 2].Width =
                new GridLength(adjustedRight, GridUnitType.Star);
            args.Handled = true;
        };
        divider.PointerReleased += (_, args) =>
        {
            if (!dragging)
            {
                return;
            }
            dragging = false;
            divider.ReleasePointerCapture(args.Pointer);
            var width = grid.ColumnDefinitions[index * 2].ActualWidth;
            var columns = (ushort)Math.Clamp(
                Math.Round(width / Math.Max(1, grid.ActualWidth) * Math.Max(1, node.BaseWidth)),
                1,
                ushort.MaxValue);
            if (FirstPaneId(node.Columns[index].Root) is { } paneId)
            {
                Mutate(
                    () => _mux.SetViewportWidth(paneId, columns),
                    $"resize viewport pane {paneId}");
            }
            args.Handled = true;
        };
        return divider;
    }

    private FrameworkElement BuildStack(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers)
    {
        if (node.PaneIds.Count == 0)
        {
            return BuildUnavailable(Loc.S("Pane_Empty"));
        }
        var expanded = node.ExpandedPaneId ?? node.PaneIds[0];
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var selector = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 4, 8, 0),
        };
        foreach (var paneId in node.PaneIds)
        {
            var button = new Button
            {
                Content = Loc.S("Pane_Pane"),
                Tag = paneId,
                Style = Application.Current.Resources["AccentButtonStyle"] as Style,
                Opacity = paneId == expanded ? 1 : 0.65,
            };
            button.Click += (_, _) =>
            {
                if (_mux.FocusPane(paneId))
                {
                    Refresh();
                }
            };
            selector.Children.Add(button);
        }
        grid.Children.Add(selector);
        var content = BuildPane(expanded, tabs, terminals, browsers);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private FrameworkElement BuildPane(
        string paneId,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers)
    {
        var paneTabs = tabs.Values
            .Where(tab => tab.PaneId == paneId)
            .OrderBy(tab => tab.Index)
            .ToList();
        if (paneTabs.Count == 0)
        {
            return BuildUnavailable(Loc.S("Pane_Empty"));
        }

        var tabView = new TabView
        {
            IsAddTabButtonVisible = true,
            TabWidthMode = TabViewWidthMode.SizeToContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        tabView.TabStripFooter = BuildPaneActions(paneId);

        TabViewItem? selected = null;
        foreach (var tab in paneTabs)
        {
            var item = new TabViewItem
            {
                Header = TabTitle(tab, terminals, browsers),
                Tag = tab.Id,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            if (tab.ContentKind == "terminal")
            {
                _renderedTerminals.Add(tab.Id);
                if (!_terminals.TryGetValue(tab.Id, out var terminal))
                {
                    terminal = new TerminalView(_mux, tab.Id);
                    terminal.GotFocus += (_, _) =>
                    {
                        if (_hostActive && ReferenceEquals(_selectedTerminal, terminal))
                        {
                            SelectedTerminalFocused?.Invoke();
                        }
                    };
                    _terminals[tab.Id] = terminal;
                }
                terminal.SetHostActive(_hostActive);
                item.Content = terminal;
            }
            else if (browsers.TryGetValue(tab.ContentId, out var browserSnapshot))
            {
                item.IconSource = new SymbolIconSource { Symbol = Symbol.World };
                _renderedBrowsers.Add(tab.Id);
                if (!_browsers.TryGetValue(tab.Id, out var browser))
                {
                    browser = new BrowserView(_mux, browserSnapshot);
                    var tabId = tab.Id;
                    browser.DocumentTitleChanged += () => UpdateBrowserTitle(tabId, browser);
                    _browsers[tab.Id] = browser;
                }
                else
                {
                    browser.Update(browserSnapshot);
                }
                item.Header = TabTitle(tab, terminals, browsers, browser.DocumentTitle);
                item.Content = browser;
            }
            else
            {
                item.Content = BuildUnavailable(Loc.S("Pane_BrowserStarting"));
            }
            item.ContextFlyout = BuildTabMenu(tab, paneTabs, paneId);
            _tabItems[tab.Id] = item;
            tabView.TabItems.Add(item);
            if (tab.Focused)
            {
                selected = item;
            }
        }
        selected ??= tabView.TabItems.OfType<TabViewItem>().First();
        tabView.SelectedItem = selected;
        var selectedTabId = selected.Tag as string;
        if (paneId == _activePaneId && selected.Content is TerminalView selectedTerminal)
        {
            _selectedTerminal = selectedTerminal;
        }

        tabView.AddTabButtonClick += (_, _) => BuildNewTabMenu(paneId).ShowAt(tabView);
        tabView.TabCloseRequested += (_, args) =>
        {
            if (args.Tab.Tag is string tabId)
            {
                Mutate(() => _mux.CloseTab(tabId), $"close tab {tabId}");
            }
        };
        tabView.SelectionChanged += (_, _) =>
        {
            if (_rendering
                || tabView.SelectedItem is not TabViewItem { Tag: string tabId } item
                || tabId == selectedTabId)
            {
                return;
            }
            if (!_mux.SelectTab(tabId))
            {
                Diag.Log($"tab selection failed: {tabId}");
                return;
            }
            selectedTabId = tabId;
            _mux.FocusPane(paneId);
            _activePaneId = paneId;
            UpdatePaneFocus();
            _selectedTerminal = item.Content as TerminalView;
            FocusSelectedTerminal("pane-tab-selected");
        };

        var border = new Border
        {
            Child = tabView,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2),
        };
        border.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => ActivatePane(paneId, tabView)),
            true);
        _paneBorders[paneId] = border;
        return border;
    }

    private bool ActivatePane(string paneId, TabView tabView)
    {
        if (!_mux.FocusPane(paneId))
        {
            return false;
        }

        _activePaneId = paneId;
        UpdatePaneFocus();
        _selectedTerminal = (tabView.SelectedItem as TabViewItem)?.Content as TerminalView;
        return true;
    }

    private MenuFlyout BuildNewTabMenu(string paneId)
    {
        var menu = new MenuFlyout();
        var terminal = new MenuFlyoutItem
        {
            Text = Loc.S("Pane_NewTerminal"),
            Icon = new SymbolIcon(Symbol.Document),
        };
        terminal.Click += (_, _) => Mutate(
            () => _mux.CreateTab(paneId),
            $"create tab in {paneId}");
        var browser = new MenuFlyoutItem
        {
            Text = Loc.S("Pane_NewBrowser"),
            Icon = new SymbolIcon(Symbol.World),
        };
        browser.Click += async (_, _) => await CreateBrowserAsync(paneId);
        menu.Items.Add(terminal);
        menu.Items.Add(browser);
        return menu;
    }

    private async System.Threading.Tasks.Task CreateBrowserAsync(string paneId)
    {
        var address = new TextBox { Text = "https://" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.S("Pane_NewBrowser"),
            Content = address,
            PrimaryButtonText = Loc.S("Browser_Open"),
            CloseButtonText = Loc.S("Action_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Mutate(
                () => _mux.CreateBrowser(paneId, address.Text.Trim()),
                $"create browser in {paneId}");
        }
    }

    private MenuFlyout BuildTabMenu(TabSnapshot tab, IReadOnlyList<TabSnapshot> paneTabs, string paneId)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = Loc.S("Tab_Rename") };
        rename.Click += async (_, _) => await RenameTabAsync(tab);
        var moveLeft = new MenuFlyoutItem
        {
            Text = Loc.S("Tab_MoveLeft"),
            IsEnabled = tab.Index > 0,
        };
        moveLeft.Click += (_, _) => MoveTab(tab, paneId, tab.Index - 1);
        var moveRight = new MenuFlyoutItem
        {
            Text = Loc.S("Tab_MoveRight"),
            IsEnabled = tab.Index < paneTabs.Count - 1,
        };
        moveRight.Click += (_, _) => MoveTab(tab, paneId, tab.Index + 1);
        var moveTo = new MenuFlyoutSubItem { Text = Loc.S("Tab_MoveToPane") };
        if (_snapshot is not null)
        {
            foreach (var pane in _snapshot.Panes.Where(candidate => candidate.Id != paneId))
            {
                var destination = new MenuFlyoutItem
                {
                    Text = string.IsNullOrWhiteSpace(pane.Name)
                        ? Loc.S("Pane_Pane")
                        : pane.Name,
                };
                destination.Click += (_, _) => MoveTab(tab, pane.Id, int.MaxValue);
                moveTo.Items.Add(destination);
            }
        }
        moveTo.IsEnabled = moveTo.Items.Count > 0;
        var close = new MenuFlyoutItem { Text = Loc.S("Tab_Close") };
        close.Click += (_, _) => Mutate(() => _mux.CloseTab(tab.Id), $"close tab {tab.Id}");
        menu.Items.Add(rename);
        menu.Items.Add(moveLeft);
        menu.Items.Add(moveRight);
        menu.Items.Add(moveTo);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(close);
        return menu;
    }

    private async System.Threading.Tasks.Task RenameTabAsync(TabSnapshot tab)
    {
        var name = new TextBox { Text = tab.Name ?? string.Empty };
        name.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.S("Tab_Rename"),
            Content = name,
            PrimaryButtonText = Loc.S("Action_Save"),
            CloseButtonText = Loc.S("Action_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var value = string.IsNullOrEmpty(name.Text) ? null : name.Text;
            Mutate(() => _mux.RenameTab(tab.Id, value), $"rename tab {tab.Id}");
        }
    }

    private void MoveTab(TabSnapshot tab, string destinationPane, int index)
    {
        if (_snapshot is null)
        {
            return;
        }
        var pane = _snapshot.Panes.FirstOrDefault(candidate => candidate.Id == destinationPane);
        var screen = pane is null
            ? null
            : _snapshot.Screens.FirstOrDefault(candidate => candidate.Id == pane.ScreenId);
        if (screen is null)
        {
            return;
        }
        var count = _snapshot.Tabs.Count(candidate => candidate.PaneId == destinationPane);
        var destinationIndex = index == int.MaxValue ? count : Math.Clamp(index, 0, count);
        Mutate(
            () => _mux.MoveTab(
                tab.Id,
                screen.WorkspaceId,
                screen.Id,
                destinationPane,
                destinationIndex),
            $"move tab {tab.Id}");
    }

    private FrameworkElement BuildPaneActions(string paneId)
    {
        var actions = new StackPanel
        {
            Width = 158,
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };
        actions.Children.Add(ActionButton(SplitIcon(false), "Pane_SplitRight", () =>
            Mutate(() => _mux.SplitPane(paneId, "right"), $"split right {paneId}")));
        actions.Children.Add(ActionButton(SplitIcon(true), "Pane_SplitDown", () =>
            Mutate(() => _mux.SplitPane(paneId, "down"), $"split down {paneId}")));
        actions.Children.Add(ActionButton(
            ActionGlyph("\uE740"),
            "Pane_Zoom",
            () => Mutate(() => _mux.ZoomPane(paneId), $"zoom pane {paneId}")));
        actions.Children.Add(ActionButton(
            ActionGlyph("\uE8AC"),
            "Pane_Rename",
            () => _ = RenamePaneAsync(paneId)));
        actions.Children.Add(ActionButton(
            ActionGlyph("\uE711"),
            "Pane_Close",
            () => Mutate(() => _mux.ClosePane(paneId), $"close pane {paneId}")));
        return actions;
    }

    private async System.Threading.Tasks.Task RenamePaneAsync(string paneId)
    {
        var current = _snapshot?.Panes.FirstOrDefault(pane => pane.Id == paneId)?.Name;
        var name = new TextBox { Text = current ?? string.Empty };
        name.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.S("Pane_Rename"),
            Content = name,
            PrimaryButtonText = Loc.S("Action_Save"),
            CloseButtonText = Loc.S("Action_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var value = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
            Mutate(() => _mux.RenamePane(paneId, value), $"rename pane {paneId}");
        }
    }

    private static FontIcon ActionGlyph(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 14,
    };

    private static FrameworkElement SplitIcon(bool down)
    {
        var icon = new Grid { Width = 12, Height = 12 };
        icon.Children.Add(new Border
        {
            Width = 8.5,
            Height = 8.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
        });
        icon.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = down ? 6 : 1,
            Height = down ? 1 : 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return icon;
    }

    private static Button ActionButton(FrameworkElement icon, string tooltipKey, Action action)
    {
        var button = new Button
        {
            Content = icon,
            Width = 30,
            Height = 30,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var name = Loc.S(tooltipKey);
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Loaded += (_, _) =>
        {
            if (icon is Grid { Children.Count: 2 } splitIcon
                && splitIcon.Children[0] is Border frame
                && splitIcon.Children[1] is Microsoft.UI.Xaml.Shapes.Rectangle divider)
            {
                frame.BorderBrush = button.Foreground;
                divider.Fill = button.Foreground;
            }
        };
        button.Click += (_, _) => action();
        return button;
    }

    private FrameworkElement BuildEmptyWorkspace()
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
        };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.S("Pane_EmptyWorkspace"),
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
        });
        var button = new Button
        {
            Content = Loc.S("Pane_NewTerminal"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => Mutate(
            () => _mux.CreateTerminal(Workspace.PublicId),
            $"create terminal in {Workspace.PublicId}");
        panel.Children.Add(button);
        return panel;
    }

    private static FrameworkElement BuildUnavailable(string message) => new Grid
    {
        Children =
        {
            new TextBlock
            {
                Text = message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            },
        },
    };

    private static string TabTitle(
        TabSnapshot tab,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals,
        IReadOnlyDictionary<string, BrowserSnapshot> browsers,
        string? documentTitle = null)
    {
        terminals.TryGetValue(tab.ContentId, out var terminal);
        browsers.TryGetValue(tab.ContentId, out var browser);
        var title = !string.IsNullOrWhiteSpace(tab.Name)
            ? tab.Name
            : !string.IsNullOrWhiteSpace(terminal?.Title)
                ? terminal.Title
                : !string.IsNullOrWhiteSpace(documentTitle)
                    ? documentTitle
                    : !string.IsNullOrWhiteSpace(browser?.Title)
                        ? browser.Title
                        : tab.ContentKind == "terminal"
                            ? Loc.S("Pane_Terminal")
                            : Loc.S("Pane_Browser");
        return terminal is { Running: false }
            ? $"{title} · {Loc.S("Terminal_Exited")}"
            : title;
    }

    private void Mutate(Func<bool> operation, string description)
    {
        if (!operation())
        {
            Diag.Log($"topology mutation failed: {description}");
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            Render(_mux.Snapshot());
            DispatcherQueue.TryEnqueue(() => FocusSelectedTerminal("topology-refreshed"));
        }
        catch (Exception ex)
        {
            Diag.Log($"topology refresh failed: {ex.Message}");
        }
    }

    private void UpdatePaneFocus()
    {
        foreach (var border in _paneBorders.Values)
        {
            border.BorderThickness = new Thickness(1);
            border.BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush;
        }
    }

    private void DisposeUnrenderedViews()
    {
        foreach (var tabId in _terminals.Keys.Where(id => !_renderedTerminals.Contains(id)).ToList())
        {
            _terminals.Remove(tabId, out var terminal);
            terminal?.Dispose();
        }
        foreach (var tabId in _browsers.Keys.Where(id => !_renderedBrowsers.Contains(id)).ToList())
        {
            _browsers.Remove(tabId, out var browser);
            browser?.Dispose();
        }
    }

    public void Relocalize()
    {
        foreach (var browser in _browsers.Values)
        {
            browser.Relocalize();
        }
        if (_snapshot is not null)
        {
            _renderKey = string.Empty;
            Render(_snapshot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var terminal in _terminals.Values)
        {
            terminal.Dispose();
        }
        _terminals.Clear();
        foreach (var browser in _browsers.Values)
        {
            browser.Dispose();
        }
        _browsers.Clear();
        Content = null;
    }
}
