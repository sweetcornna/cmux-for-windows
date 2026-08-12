using System;
using System.Collections.Generic;

namespace CmuxGui.Input;

[Flags]
internal enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

[Flags]
internal enum ShortcutContexts
{
    None = 0,
    Terminal = 1,
    Browser = 2,
    Workspace = Terminal | Browser,
}

internal enum ShortcutOwner
{
    MainWindow,
    Workspace,
}

internal enum ShortcutAction
{
    FocusWorkspaceSearch,
    OpenSettings,
    NewWorkspace,
    PreviousWorkspace,
    NextWorkspace,
    SelectWorkspace,
    MoveWorkspaceUp,
    MoveWorkspaceDown,
    RenameWorkspace,
    CloseWorkspace,
    NewScreen,
    PreviousScreen,
    NextScreen,
    SelectScreen,
    RenameScreen,
    CloseScreen,
    SplitPaneRight,
    SplitPaneDown,
    FocusPaneLeft,
    FocusPaneRight,
    FocusPaneUp,
    FocusPaneDown,
    TogglePaneZoom,
    RenamePane,
    ClosePane,
    NewTerminalTab,
    NewBrowserTab,
    PreviousTab,
    NextTab,
    SelectTab,
    MoveTabLeft,
    MoveTabRight,
    MoveTabToPane,
    RenameTab,
    CloseTab,
    BrowserBack,
    BrowserForward,
    BrowserReload,
    BrowserFocusAddress,
    TerminalCopy,
    TerminalPaste,
    TerminalSelectAll,
}

internal readonly record struct ShortcutStroke(
    int VirtualKey,
    ShortcutModifiers Modifiers,
    bool IsAltGr = false);

internal readonly record struct ShortcutMatch(
    ShortcutAction Action,
    ShortcutOwner Owner,
    int Index,
    bool Destructive,
    string Label);

internal static class ShortcutDefinitions
{
    private const int KeyTab = 0x09;
    private const int KeyEnter = 0x0D;
    private const int KeyInsert = 0x2D;
    private const int KeyLeft = 0x25;
    private const int KeyUp = 0x26;
    private const int KeyRight = 0x27;
    private const int KeyDown = 0x28;
    private const int KeyPageUp = 0x21;
    private const int KeyPageDown = 0x22;
    private const int KeyF2 = 0x71;
    private const int KeyF5 = 0x74;
    private const int KeyMinus = 0xBD;
    private const int KeyBackslash = 0xDC;

    private readonly record struct Definition(
        int Key,
        ShortcutModifiers Modifiers,
        ShortcutContexts Contexts,
        ShortcutAction Action,
        ShortcutOwner Owner,
        bool Destructive,
        string Label);

    private static readonly Definition[] Definitions =
    [
        D('K', C | S, W, ShortcutAction.FocusWorkspaceSearch, M, false, "Ctrl+Shift+K"),
        D(0xBC, C, W, ShortcutAction.OpenSettings, M, false, "Ctrl+,"),
        D('N', C | S, W, ShortcutAction.NewWorkspace, M, false, "Ctrl+Shift+N"),
        D(KeyPageUp, C | S, W, ShortcutAction.PreviousWorkspace, M, false, "Ctrl+Shift+PageUp"),
        D(KeyPageDown, C | S, W, ShortcutAction.NextWorkspace, M, false, "Ctrl+Shift+PageDown"),
        D(KeyUp, C | A | S, W, ShortcutAction.MoveWorkspaceUp, M, false, "Ctrl+Alt+Shift+Up"),
        D(KeyDown, C | A | S, W, ShortcutAction.MoveWorkspaceDown, M, false, "Ctrl+Alt+Shift+Down"),
        D(KeyF2, C | A | S, W, ShortcutAction.RenameWorkspace, M, false, "Ctrl+Alt+Shift+F2"),
        D('W', C | A | S, W, ShortcutAction.CloseWorkspace, M, true, "Ctrl+Alt+Shift+W"),

        D('N', C | A, W, ShortcutAction.NewScreen, V, false, "Ctrl+Alt+N"),
        D(KeyPageUp, C | A, W, ShortcutAction.PreviousScreen, V, false, "Ctrl+Alt+PageUp"),
        D(KeyPageDown, C | A, W, ShortcutAction.NextScreen, V, false, "Ctrl+Alt+PageDown"),
        D(KeyF2, C | A, W, ShortcutAction.RenameScreen, V, false, "Ctrl+Alt+F2"),
        D('W', C | A, W, ShortcutAction.CloseScreen, V, true, "Ctrl+Alt+W"),

        D(KeyBackslash, C | S, W, ShortcutAction.SplitPaneRight, V, false, "Ctrl+Shift+\\"),
        D(KeyMinus, C | S, W, ShortcutAction.SplitPaneDown, V, false, "Ctrl+Shift+-"),
        D(KeyLeft, C | A, W, ShortcutAction.FocusPaneLeft, V, false, "Ctrl+Alt+Left"),
        D(KeyRight, C | A, W, ShortcutAction.FocusPaneRight, V, false, "Ctrl+Alt+Right"),
        D(KeyUp, C | A, W, ShortcutAction.FocusPaneUp, V, false, "Ctrl+Alt+Up"),
        D(KeyDown, C | A, W, ShortcutAction.FocusPaneDown, V, false, "Ctrl+Alt+Down"),
        D(KeyEnter, C | S, W, ShortcutAction.TogglePaneZoom, V, false, "Ctrl+Shift+Enter"),
        D(KeyF2, C | S, W, ShortcutAction.RenamePane, V, false, "Ctrl+Shift+F2"),
        D('W', C | S, W, ShortcutAction.ClosePane, V, true, "Ctrl+Shift+W"),

        D('T', C, W, ShortcutAction.NewTerminalTab, V, false, "Ctrl+T"),
        D('T', C | S, W, ShortcutAction.NewBrowserTab, V, false, "Ctrl+Shift+T"),
        D(KeyTab, C | S, W, ShortcutAction.PreviousTab, V, false, "Ctrl+Shift+Tab"),
        D(KeyTab, C, W, ShortcutAction.NextTab, V, false, "Ctrl+Tab"),
        D(KeyLeft, C | A | S, W, ShortcutAction.MoveTabLeft, V, false, "Ctrl+Alt+Shift+Left"),
        D(KeyRight, C | A | S, W, ShortcutAction.MoveTabRight, V, false, "Ctrl+Alt+Shift+Right"),
        D(KeyF2, C, W, ShortcutAction.RenameTab, V, false, "Ctrl+F2"),
        D('W', C, W, ShortcutAction.CloseTab, V, true, "Ctrl+W"),

        D(KeyLeft, A, B, ShortcutAction.BrowserBack, V, false, "Alt+Left"),
        D(KeyRight, A, B, ShortcutAction.BrowserForward, V, false, "Alt+Right"),
        D('R', C, B, ShortcutAction.BrowserReload, V, false, "Ctrl+R"),
        D(KeyF5, ShortcutModifiers.None, B, ShortcutAction.BrowserReload, V, false, "F5"),
        D('L', C, B, ShortcutAction.BrowserFocusAddress, V, false, "Ctrl+L"),

        D('C', C | S, T, ShortcutAction.TerminalCopy, V, false, "Ctrl+Shift+C"),
        D(KeyInsert, C, T, ShortcutAction.TerminalCopy, V, false, "Ctrl+Insert"),
        D('V', C | S, T, ShortcutAction.TerminalPaste, V, false, "Ctrl+Shift+V"),
        D(KeyInsert, S, T, ShortcutAction.TerminalPaste, V, false, "Shift+Insert"),
        D('A', C | S, T, ShortcutAction.TerminalSelectAll, V, false, "Ctrl+Shift+A"),
    ];

    private const ShortcutModifiers C = ShortcutModifiers.Control;
    private const ShortcutModifiers A = ShortcutModifiers.Alt;
    private const ShortcutModifiers S = ShortcutModifiers.Shift;
    private const ShortcutContexts T = ShortcutContexts.Terminal;
    private const ShortcutContexts B = ShortcutContexts.Browser;
    private const ShortcutContexts W = ShortcutContexts.Workspace;
    private const ShortcutOwner M = ShortcutOwner.MainWindow;
    private const ShortcutOwner V = ShortcutOwner.Workspace;

    internal static ShortcutMatch? Match(ShortcutStroke stroke, ShortcutContexts context)
    {
        if (stroke.IsAltGr || context == ShortcutContexts.None)
        {
            return null;
        }

        if (stroke.VirtualKey is >= '0' and <= '9')
        {
            var index = stroke.VirtualKey == '0' ? 9 : stroke.VirtualKey - '1';
            if (stroke.Modifiers == C)
            {
                return new(ShortcutAction.SelectTab, V, index, false, $"Ctrl+{(char)stroke.VirtualKey}");
            }
            if (stroke.Modifiers == (C | A))
            {
                return new(ShortcutAction.SelectScreen, V, index, false, $"Ctrl+Alt+{(char)stroke.VirtualKey}");
            }
            if (stroke.Modifiers == (C | S))
            {
                return new(ShortcutAction.SelectWorkspace, M, index, false, $"Ctrl+Shift+{(char)stroke.VirtualKey}");
            }
            if (stroke.Modifiers == (C | A | S))
            {
                return new(ShortcutAction.MoveTabToPane, V, index, false, $"Ctrl+Alt+Shift+{(char)stroke.VirtualKey}");
            }
        }

        foreach (var definition in Definitions)
        {
            if (definition.Key == stroke.VirtualKey
                && definition.Modifiers == stroke.Modifiers
                && (definition.Contexts & context) != 0)
            {
                return new(
                    definition.Action,
                    definition.Owner,
                    -1,
                    definition.Destructive,
                    definition.Label);
            }
        }
        return null;
    }

    internal static IReadOnlyList<string> ValidateDefinitions()
    {
        var errors = new List<string>();
        for (var index = 0; index < Definitions.Length; index++)
        {
            var current = Definitions[index];
            if (string.IsNullOrWhiteSpace(current.Label))
            {
                errors.Add($"{current.Action} has no label");
            }
            if (current.Action.ToString().StartsWith("Close", StringComparison.Ordinal)
                && !current.Destructive)
            {
                errors.Add($"{current.Action} is not marked destructive");
            }
            for (var otherIndex = index + 1; otherIndex < Definitions.Length; otherIndex++)
            {
                var other = Definitions[otherIndex];
                if (current.Key == other.Key
                    && current.Modifiers == other.Modifiers
                    && (current.Contexts & other.Contexts) != 0)
                {
                    errors.Add($"{current.Label} conflicts with {other.Label}");
                }
            }
        }
        return errors;
    }

    internal static IReadOnlyList<ShortcutAction> DefinedActions()
    {
        var actions = new List<ShortcutAction>();
        foreach (var definition in Definitions)
        {
            if (!actions.Contains(definition.Action))
            {
                actions.Add(definition.Action);
            }
        }
        actions.Add(ShortcutAction.SelectWorkspace);
        actions.Add(ShortcutAction.SelectScreen);
        actions.Add(ShortcutAction.SelectTab);
        actions.Add(ShortcutAction.MoveTabToPane);
        return actions;
    }

    private static Definition D(
        int key,
        ShortcutModifiers modifiers,
        ShortcutContexts contexts,
        ShortcutAction action,
        ShortcutOwner owner,
        bool destructive,
        string label) => new(key, modifiers, contexts, action, owner, destructive, label);
}
