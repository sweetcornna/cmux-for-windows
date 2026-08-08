using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CmuxGui.Services;

/// <summary>
/// Discovery for Ghostty-format themes.
///
/// Two sources, in Ghostty's own precedence order: the user's
/// `themes` directory wins over what cmux bundles, so a user who maintains
/// their own copy of a theme keeps it. Parsing lives in Rust; this side only
/// finds files and hands over their text.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>Theme names, sorted, with duplicates resolved in favour of the user's.</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directories())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        return names.ToList();
    }

    /// <summary>Read a theme's text, or null when no such theme exists.</summary>
    public static string? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name.Contains(".."))
        {
            // Theme names index a directory; refuse anything path-like.
            return null;
        }

        foreach (var dir in Directories())
        {
            foreach (var candidate in new[] { Path.Combine(dir, name), Path.Combine(dir, name + ".conf") })
            {
                if (File.Exists(candidate))
                {
                    try
                    {
                        return File.ReadAllText(candidate);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log($"theme read failed for '{name}': {ex.Message}");
                        return null;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>User themes first, then the bundled set.</summary>
    private static IEnumerable<string> Directories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "ghostty", "themes");
        }
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");
    }
}
