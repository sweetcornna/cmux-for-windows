using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CmuxGui.Services;

/// <summary>How the window paints behind its content.</summary>
public enum BackdropKind
{
    /// <summary>Mica: opaque-feeling, tinted by the desktop. The Settings look.</summary>
    Mica,
    /// <summary>Acrylic: translucent and blurred, tunable.</summary>
    Acrylic,
    /// <summary>No system backdrop; the terminal colour shows through.</summary>
    None,
}

/// <summary>
/// User preferences for the GUI shell.
///
/// Deliberately separate from the Ghostty config: that file belongs to the
/// user's terminal setup and cmux only reads it. Anything cmux itself owns —
/// which theme is selected, window transparency, language — lives here so the
/// app never writes to a file it does not own.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Theme file name without extension, or empty to follow the Ghostty config.</summary>
    public string Theme { get; set; } = string.Empty;

    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>Terminal background alpha, 0.0 fully transparent to 1.0 opaque.</summary>
    public double TerminalOpacity { get; set; } = 1.0;

    /// <summary>Acrylic blur strength, 0.0 to 1.0. Only meaningful for Acrylic.</summary>
    public double BlurAmount { get; set; } = 0.5;

    /// <summary>Absolute path to a background image, or empty for none.</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>Background image alpha, so text stays readable over a photo.</summary>
    public double BackgroundImageOpacity { get; set; } = 0.25;

    /// <summary>BCP-47 tag, or empty to follow the system language.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Element tint overrides, hex "#RRGGBB"; empty means use the theme.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Element theme: empty (system), "Light", or "Dark".</summary>
    ///
    /// Defaults to Dark rather than following the system. A terminal is a dark
    /// surface, and a light Fluent shell wrapped around a black rectangle reads
    /// as two unrelated things bolted together. cmux itself is a dark app.
    public string AppTheme { get; set; } = "Dark";

    [JsonIgnore]
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cmux",
        "gui-settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings must not stop the app launching;
            // defaults are always a usable state.
            Diag.Log($"settings load failed, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Diag.Log($"settings save failed: {ex.Message}");
        }
    }

    /// <summary>Process-wide instance, loaded once at startup.</summary>
    public static AppSettings Current { get; } = Load();

    /// <summary>Raised after any setting changes, so open surfaces can repaint.</summary>
    public static event Action? Changed;

    public void NotifyChanged()
    {
        Save();
        Changed?.Invoke();
    }
}
