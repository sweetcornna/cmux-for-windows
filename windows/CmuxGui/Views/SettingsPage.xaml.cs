using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CmuxGui.Interop;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage.Pickers;

namespace CmuxGui.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ResourceLoader _res = new();
    private readonly AppSettings _settings = AppSettings.Current;

    /// <summary>Language options as (display name, BCP-47 tag); empty tag follows the system.</summary>
    private static readonly (string Display, string Tag)[] Languages =
    {
        (string.Empty, string.Empty),
        ("English", "en-US"),
        ("简体中文", "zh-Hans"),
    };

    /// <summary>Suppresses change handlers while controls are being populated.</summary>
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Localize();
        Populate();
        _loading = false;
    }

    private string S(string key) => _res.GetString(key);

    private void Localize()
    {
        TitleText.Text = S("Settings_Title");
        AppearanceHeader.Text = S("Settings_Appearance");
        ThemeLabel.Text = S("Settings_Theme");
        ThemeDesc.Text = S("Settings_ThemeDesc");
        BackgroundLabel.Text = S("Settings_Background");
        ChooseImageButton.Content = S("Settings_Choose");
        ClearImageButton.Content = S("Settings_Clear");
        TerminalHeader.Text = S("Settings_Terminal");
        FontLabel.Text = S("Settings_Font");
        FontDesc.Text = S("Settings_FontDesc");
        IntegrationHeader.Text = S("Settings_Integration");
        ContextMenuLabel.Text = S("Settings_ContextMenu");
        ContextMenuDesc.Text = S("Settings_ContextMenuDesc");
        LanguageLabel.Text = S("Settings_Language");
        LanguageDesc.Text = S("Settings_LanguageDesc");
        AboutHeader.Text = S("Settings_About");
        AboutDesc.Text = S("Settings_AboutDesc");

        OpacitySlider.Header = S("Settings_Opacity");
        BlurSlider.Header = S("Settings_Blur");
        ImageOpacitySlider.Header = S("Settings_ImageOpacity");
        AppThemeCombo.Header = S("Settings_AppTheme");
        BackdropCombo.Header = S("Settings_Backdrop");

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"Version {version.ToString(3)}";
    }

    private void Populate()
    {
        // Terminal themes: bundled plus the user's Ghostty themes directory.
        ThemeCombo.Items.Add(S("Settings_FollowConfig"));
        foreach (var name in ThemeCatalog.Names())
        {
            ThemeCombo.Items.Add(name);
        }
        var themeIndex = string.IsNullOrWhiteSpace(_settings.Theme)
            ? 0
            : ThemeCombo.Items.IndexOf(_settings.Theme);
        ThemeCombo.SelectedIndex = themeIndex < 0 ? 0 : themeIndex;

        foreach (var label in new[] { S("Option_System"), S("Option_Light"), S("Option_Dark") })
        {
            AppThemeCombo.Items.Add(label);
        }
        AppThemeCombo.SelectedIndex = _settings.AppTheme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };

        foreach (var label in new[] { S("Option_Mica"), S("Option_Acrylic"), S("Option_None") })
        {
            BackdropCombo.Items.Add(label);
        }
        BackdropCombo.SelectedIndex = (int)_settings.Backdrop;

        OpacitySlider.Value = _settings.TerminalOpacity * 100.0;
        BlurSlider.Value = _settings.BlurAmount * 100.0;
        ImageOpacitySlider.Value = _settings.BackgroundImageOpacity * 100.0;
        UpdateBackgroundPathText();

        foreach (var (display, _) in Languages)
        {
            LanguageCombo.Items.Add(string.IsNullOrEmpty(display) ? S("Option_System") : display);
        }
        var langIndex = Array.FindIndex(Languages, l => l.Tag == _settings.Language);
        LanguageCombo.SelectedIndex = langIndex < 0 ? 0 : langIndex;

        ContextMenuToggle.IsOn = ShellIntegration.IsRegistered;

        // Font is read-only here: it belongs to the Ghostty config, which cmux
        // does not write to.
        CmuxNative.ThemeLoad(out var theme);
        var family = CmuxNative.FontFamilyOf(theme);
        FontValueText.Text = string.IsNullOrWhiteSpace(family) ? "Cascadia Mono" : family;
    }

    private void UpdateBackgroundPathText()
    {
        BackgroundPathText.Text = string.IsNullOrWhiteSpace(_settings.BackgroundImagePath)
            ? S("Settings_BackgroundDesc")
            : Path.GetFileName(_settings.BackgroundImagePath);
    }

    private void Commit() => _settings.NotifyChanged();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.Theme = ThemeCombo.SelectedIndex <= 0
            ? string.Empty
            : ThemeCombo.SelectedItem as string ?? string.Empty;
        Commit();
    }

    private void OnAppThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.AppTheme = AppThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => string.Empty,
        };
        // Applying to the root element retints the whole visual tree at once.
        if (XamlRoot?.Content is FrameworkElement root)
        {
            root.RequestedTheme = _settings.AppTheme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        Commit();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.Backdrop = (BackdropKind)Math.Max(0, BackdropCombo.SelectedIndex);
        Commit();
    }

    private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.TerminalOpacity = e.NewValue / 100.0;
        Commit();
    }

    private void OnBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.BlurAmount = e.NewValue / 100.0;
        Commit();
    }

    private void OnImageOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _settings.BackgroundImageOpacity = e.NewValue / 100.0;
        Commit();
    }

    private async void OnChooseImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
            {
                picker.FileTypeFilter.Add(ext);
            }
            // Unpackaged apps have no implicit window for the dialog to parent
            // to, so it must be given one explicitly.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                _settings.BackgroundImagePath = file.Path;
                UpdateBackgroundPathText();
                Commit();
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"image picker failed: {ex.Message}");
        }
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundImagePath = string.Empty;
        UpdateBackgroundPathText();
        Commit();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var index = Math.Clamp(LanguageCombo.SelectedIndex, 0, Languages.Length - 1);
        _settings.Language = Languages[index].Tag;
        Commit();
    }

    private void OnContextMenuToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var ok = ContextMenuToggle.IsOn
            ? ShellIntegration.Register(S("ContextMenu_OpenWindow"), S("ContextMenu_OpenWorkspace"))
            : ShellIntegration.Unregister();

        ContextMenuStatus.Text = ok ? string.Empty : S("Settings_ContextMenuFailed");
        if (!ok)
        {
            // Reflect reality rather than leaving the switch claiming success.
            ContextMenuToggle.IsOn = ShellIntegration.IsRegistered;
        }
    }
}
