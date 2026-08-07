using System;
using CmuxGui.Controls;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace CmuxGui;

public sealed partial class MainWindow : Window
{
    private readonly ResourceLoader _res = new();
    private int _tabCounter;

    public MainWindow()
    {
        InitializeComponent();

        // Let the app own the caption area so the title bar reads as part of
        // the window rather than a separate system strip.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        NavSearch.PlaceholderText = _res.GetString("Nav_Search");
        WorkspacesHeader.Content = _res.GetString("Nav_Workspaces");

        ApplyAppearance();
        AppSettings.Changed += ApplyAppearance;

        AddTerminalTab();
    }

    /// <summary>Apply the window-level parts of the appearance settings.</summary>
    private void ApplyAppearance()
    {
        var settings = AppSettings.Current;

        SystemBackdrop = settings.Backdrop switch
        {
            BackdropKind.Mica => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            // Acrylic is the blurred one; its tint opacity is what "blur
            // amount" maps to, since WinUI exposes no blur radius directly.
            BackdropKind.Acrylic => new DesktopAcrylicBackdrop(),
            _ => null,
        };

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

    private void AddTerminalTab()
    {
        _tabCounter++;
        var view = new TerminalView();
        var tab = new TabViewItem
        {
            Header = $"PowerShell {_tabCounter}",
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
            Content = view,
            // TabViewItem aligns content to the top by default, which hands the
            // child its *desired* height. A CanvasControl has no intrinsic size,
            // so it asks for zero and renders nothing. Stretch is required.
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    private void OnAddTab(TabView sender, object args) => AddTerminalTab();

    private void OnCloseTab(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Content is IDisposable disposable)
        {
            disposable.Dispose();
        }
        sender.TabItems.Remove(args.Tab);

        // Closing the last tab closes the window, matching terminal convention.
        if (sender.TabItems.Count == 0)
        {
            Close();
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Workspace destinations arrive once the engine exposes them across the
        // FFI boundary; Settings is wired up now.
        if (args.IsSettingsSelected)
        {
            SettingsFrame.Navigate(typeof(Views.SettingsPage));
            SettingsFrame.Visibility = Visibility.Visible;
            Tabs.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsFrame.Visibility = Visibility.Collapsed;
            Tabs.Visibility = Visibility.Visible;
        }
    }
}
