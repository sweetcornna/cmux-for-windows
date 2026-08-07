using System;
using CmuxGui.Controls;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CmuxGui;

public sealed partial class MainWindow : Window
{
    private int _tabCounter;
    /// <summary>Guards the two-way sync between nav selection and tab selection.</summary>
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();

        // Let the app own the caption area so the title bar reads as part of
        // the window rather than a separate system strip.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Relocalize();

        ApplyAppearance();
        AppSettings.Changed += ApplyAppearance;
        AppSettings.Changed += Relocalize;

        Tabs.SelectionChanged += OnTabSelectionChanged;
        AddTerminalTab();
    }

    /// <summary>Re-read chrome strings, so a language change shows up at once.</summary>
    private void Relocalize()
    {
        NavSearch.PlaceholderText = Loc.S("Nav_Search");
        WorkspacesHeader.Content = Loc.S("Nav_Workspaces");
    }

    /// <summary>Apply the window-level parts of the appearance settings.</summary>
    private void ApplyAppearance()
    {
        var settings = AppSettings.Current;

        var hasCustomBackground = !string.IsNullOrWhiteSpace(settings.AppBackgroundColor)
            || (!string.IsNullOrWhiteSpace(settings.AppImagePath)
                && System.IO.File.Exists(settings.AppImagePath));

        // A system backdrop paints over anything behind it, so a custom colour
        // or image can only show if the backdrop is switched off.
        SystemBackdrop = hasCustomBackground
            ? null
            : settings.Backdrop switch
            {
                BackdropKind.Mica => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                // Acrylic is the blurred one; its tint opacity is what "blur
                // amount" maps to, since WinUI exposes no blur radius directly.
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

    private void AddTerminalTab()
    {
        _tabCounter++;
        var title = $"PowerShell {_tabCounter}";
        var view = new TerminalView();
        var tab = new TabViewItem
        {
            Header = title,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
            Content = view,
            // TabViewItem aligns content to the top by default, which hands the
            // child its *desired* height. A CanvasControl has no intrinsic size,
            // so it asks for zero and renders nothing. Stretch is required.
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Tabs.TabItems.Add(tab);

        // Mirror the tab into the sidebar. cmux's pane is a session list, so an
        // empty rail reads as unfinished rather than minimal.
        Nav.MenuItems.Add(BuildSessionItem(title, tab));

        Tabs.SelectedItem = tab;
    }

    /// <summary>A sidebar row: title over its working directory, like cmux.</summary>
    private static NavigationViewItem BuildSessionItem(string title, TabViewItem tab)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(new TextBlock
        {
            // The session's actual directory, not a hardcoded guess.
            Text = App.LaunchFolder is { Length: > 0 } folder
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new NavigationViewItem
        {
            Content = text,
            Icon = new SymbolIcon(Symbol.Document),
            Tag = tab,
        };
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Tabs.SelectedItem is not TabViewItem tab)
        {
            return;
        }
        _syncing = true;
        try
        {
            foreach (var item in Nav.MenuItems)
            {
                if (item is NavigationViewItem nav && ReferenceEquals(nav.Tag, tab))
                {
                    Nav.SelectedItem = nav;
                    break;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnAddTab(TabView sender, object args) => AddTerminalTab();

    private void OnCloseTab(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Content is IDisposable disposable)
        {
            disposable.Dispose();
        }
        sender.TabItems.Remove(args.Tab);

        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem nav && ReferenceEquals(nav.Tag, args.Tab))
            {
                Nav.MenuItems.Remove(nav);
                break;
            }
        }

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
            return;
        }

        SettingsFrame.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;

        if (_syncing || args.SelectedItem is not NavigationViewItem { Tag: TabViewItem tab })
        {
            return;
        }
        _syncing = true;
        try
        {
            Tabs.SelectedItem = tab;
        }
        finally
        {
            _syncing = false;
        }
    }
}
