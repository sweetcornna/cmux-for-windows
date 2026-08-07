using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CmuxGui.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Version unknown" : $"Version {version.ToString(3)}";
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Applying to the root element keeps the whole visual tree consistent;
        // Default means "follow the system", which is what Settings does.
        if (Content is not FrameworkElement root)
        {
            return;
        }
        root.RequestedTheme = ThemeCombo.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
