using System;
using System.Collections.Generic;
using System.Linq;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CmuxGui.Controls;

internal sealed class WorkspaceView : UserControl, IDisposable
{
    private readonly MuxRuntime _mux;
    private readonly Dictionary<string, TerminalView> _terminals = [];
    private readonly Dictionary<string, Border> _paneBorders = [];
    private bool _rendering;
    private bool _disposed;
    private string? _activePaneId;
    private TerminalView? _selectedTerminal;

    public WorkspaceView(MuxRuntime mux, MuxRuntime.WorkspaceInfo workspace)
    {
        _mux = mux;
        Workspace = workspace;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    public MuxRuntime.WorkspaceInfo Workspace { get; }

    public void Render(MuxSnapshot snapshot)
    {
        _rendering = true;
        try
        {
            DisposeTerminals();
            _paneBorders.Clear();
            _selectedTerminal = null;

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
            _activePaneId = screen.Layout.ActivePaneId;

            Content = !string.IsNullOrWhiteSpace(screen.Layout.ZoomedPaneId)
                ? BuildPane(screen.Layout.ZoomedPaneId!, tabs, terminals)
                : BuildNode(screen.Layout.Root, tabs, terminals);
            UpdatePaneFocus();
        }
        finally
        {
            _rendering = false;
        }
    }

    public void FocusSelectedTerminal(string reason) =>
        _selectedTerminal?.FocusTerminal(reason);

    private FrameworkElement BuildNode(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals) => node.Kind switch
    {
        "leaf" when node.PaneId is not null => BuildPane(node.PaneId, tabs, terminals),
        "split" when node.First is not null && node.Second is not null =>
            BuildSplit(node, tabs, terminals),
        "viewport" => BuildViewport(node, tabs, terminals),
        "stack" => BuildStack(node, tabs, terminals),
        _ => BuildUnavailable(Loc.S("Pane_Empty")),
    };

    private FrameworkElement BuildSplit(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals)
    {
        var horizontal = node.Direction == "horizontal";
        var ratio = Math.Clamp(node.Ratio, 0.05, 0.95);
        var grid = new Grid();
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });
        }

        var first = BuildNode(node.First!, tabs, terminals);
        var second = BuildNode(node.Second!, tabs, terminals);
        grid.Children.Add(first);
        grid.Children.Add(second);
        if (horizontal)
        {
            Grid.SetColumn(second, 1);
        }
        else
        {
            Grid.SetRow(second, 1);
        }
        return grid;
    }

    private FrameworkElement BuildViewport(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals)
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
                Width = new GridLength(Math.Max(0.05, column.Width), GridUnitType.Star),
            });
            var content = BuildNode(column.Root, tabs, terminals);
            Grid.SetColumn(content, index);
            grid.Children.Add(content);
        }
        return grid;
    }

    private FrameworkElement BuildStack(
        LayoutNodeSnapshot node,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals)
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
        var content = BuildPane(expanded, tabs, terminals);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private FrameworkElement BuildPane(
        string paneId,
        IReadOnlyDictionary<string, TabSnapshot> tabs,
        IReadOnlyDictionary<string, TerminalSnapshot> terminals)
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
                Header = TabTitle(tab, terminals),
                Tag = tab.Id,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            if (tab.ContentKind == "terminal")
            {
                var terminal = new TerminalView(_mux, tab.Id);
                _terminals[tab.Id] = terminal;
                item.Content = terminal;
            }
            else
            {
                item.Content = BuildUnavailable(Loc.S("Pane_BrowserUnavailable"));
            }
            tabView.TabItems.Add(item);
            if (tab.Focused)
            {
                selected = item;
            }
        }
        selected ??= tabView.TabItems.OfType<TabViewItem>().First();
        tabView.SelectedItem = selected;
        if (paneId == _activePaneId && selected.Content is TerminalView selectedTerminal)
        {
            _selectedTerminal = selectedTerminal;
        }

        tabView.AddTabButtonClick += (_, _) => Mutate(
            () => _mux.CreateTab(paneId),
            $"create tab in {paneId}");
        tabView.TabCloseRequested += (_, args) =>
        {
            if (args.Tab.Tag is string tabId)
            {
                Mutate(() => _mux.CloseTab(tabId), $"close tab {tabId}");
            }
        };
        tabView.SelectionChanged += (_, _) =>
        {
            if (_rendering || tabView.SelectedItem is not TabViewItem { Tag: string tabId } item)
            {
                return;
            }
            if (!_mux.SelectTab(tabId))
            {
                Diag.Log($"tab selection failed: {tabId}");
                return;
            }
            _mux.FocusPane(paneId);
            _activePaneId = paneId;
            UpdatePaneFocus();
            _selectedTerminal = item.Content as TerminalView;
            _selectedTerminal?.FocusTerminal("pane-tab-selected");
        };

        var border = new Border
        {
            Child = tabView,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2),
        };
        border.PointerPressed += (_, _) =>
        {
            if (_mux.FocusPane(paneId))
            {
                _activePaneId = paneId;
                UpdatePaneFocus();
                _selectedTerminal = (tabView.SelectedItem as TabViewItem)?.Content as TerminalView;
            }
        };
        _paneBorders[paneId] = border;
        return border;
    }

    private FrameworkElement BuildPaneActions(string paneId)
    {
        var actions = new StackPanel
        {
            Width = 94,
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };
        actions.Children.Add(ActionButton(SplitIcon(false), "Pane_SplitRight", () =>
            Mutate(() => _mux.SplitPane(paneId, false), $"split right {paneId}")));
        actions.Children.Add(ActionButton(SplitIcon(true), "Pane_SplitDown", () =>
            Mutate(() => _mux.SplitPane(paneId, true), $"split down {paneId}")));
        actions.Children.Add(ActionButton(
            new FontIcon { Glyph = "\uE711", FontSize = 12 },
            "Pane_Close",
            () => Mutate(() => _mux.ClosePane(paneId), $"close pane {paneId}")));
        return actions;
    }

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
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
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
        IReadOnlyDictionary<string, TerminalSnapshot> terminals)
    {
        if (!string.IsNullOrWhiteSpace(tab.Name))
        {
            return tab.Name;
        }
        if (terminals.TryGetValue(tab.ContentId, out var terminal)
            && !string.IsNullOrWhiteSpace(terminal.Title))
        {
            return terminal.Title;
        }
        return tab.ContentKind == "terminal"
            ? Loc.S("Pane_Terminal")
            : Loc.S("Pane_Browser");
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

    private void DisposeTerminals()
    {
        foreach (var terminal in _terminals.Values)
        {
            terminal.Dispose();
        }
        _terminals.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DisposeTerminals();
        Content = null;
    }
}
