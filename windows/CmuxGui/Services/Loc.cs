using System;
using Microsoft.Windows.ApplicationModel.Resources;

namespace CmuxGui.Services;

/// <summary>
/// String lookup with an explicit language.
///
/// <c>ResourceLoader</c>'s default context resolves against the system language
/// list and does not follow <c>ApplicationLanguages.PrimaryLanguageOverride</c>,
/// so a user whose list starts with en-US kept seeing English however the
/// setting was written. Building a context and setting the Language qualifier
/// directly is what actually forces the choice.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager = new();
    private static ResourceMap? _map;
    private static ResourceContext _context = null!;

    static Loc()
    {
        // resw files become subtrees named after the file. Fall back to the
        // root map so a rename does not silently blank every label.
        try
        {
            _map = Manager.MainResourceMap.GetSubtree("Resources");
        }
        catch (Exception ex)
        {
            Diag.Log($"resource subtree 'Resources' missing: {ex.Message}");
            _map = Manager.MainResourceMap;
        }
        SetLanguage(AppSettings.Current.Language);
    }

    /// <summary>Language currently used for lookups; empty means follow the system.</summary>
    public static string Language { get; private set; } = string.Empty;

    public static void SetLanguage(string tag)
    {
        Language = tag ?? string.Empty;
        _context = Manager.CreateResourceContext();
        if (!string.IsNullOrWhiteSpace(Language))
        {
            _context.QualifierValues["Language"] = Language;
        }
        Diag.Log($"Loc language set to '{(string.IsNullOrEmpty(Language) ? "system" : Language)}'");
    }

    /// <summary>Localized string, or the key itself when lookup fails.</summary>
    public static string S(string key)
    {
        try
        {
            var value = _map?.GetValue(key, _context)?.ValueAsString;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex)
        {
            // Showing the key beats showing nothing, and names the culprit.
            Diag.Log($"missing string '{key}': {ex.Message}");
            return key;
        }
    }
}
