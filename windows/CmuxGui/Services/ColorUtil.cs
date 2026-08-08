using System;
using System.Globalization;
using Windows.UI;

namespace CmuxGui.Services;

/// <summary>Conversions between the "#RRGGBB" form stored in settings and <see cref="Color"/>.</summary>
public static class ColorUtil
{
    /// <summary>Parse "#RRGGBB" or "RRGGBB"; null when absent or malformed.</summary>
    public static Color? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return null;
        }
        return Color.FromArgb(
            255,
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    public static Color ParseOr(string? value, Color fallback) => Parse(value) ?? fallback;

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Same colour at a given alpha, for scrims.</summary>
    public static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(opacity * 255.0, 0, 255), color.R, color.G, color.B);
}
