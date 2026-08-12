using CmuxGui.Input;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static ShortcutMatch Match(
    int key,
    ShortcutModifiers modifiers,
    ShortcutContexts context,
    ShortcutAction action,
    int index = -1)
{
    var match = ShortcutDefinitions.Match(new(key, modifiers), context)
        ?? throw new InvalidOperationException($"No match for {key:X2} {modifiers} in {context}");
    Check(match.Action == action, $"{match.Label} matched {match.Action}, expected {action}");
    Check(match.Index == index, $"{match.Label} index {match.Index}, expected {index}");
    return match;
}

var errors = ShortcutDefinitions.ValidateDefinitions();
Check(errors.Count == 0, string.Join(Environment.NewLine, errors));

var c = ShortcutModifiers.Control;
var a = ShortcutModifiers.Alt;
var s = ShortcutModifiers.Shift;
var w = ShortcutContexts.Terminal;
var b = ShortcutContexts.Browser;

var expected = new (int Key, ShortcutModifiers Modifiers, ShortcutContexts Context, ShortcutAction Action)[]
{
    ('K', c | s, w, ShortcutAction.FocusWorkspaceSearch),
    (0xBC, c, w, ShortcutAction.OpenSettings),
    ('N', c | s, w, ShortcutAction.NewWorkspace),
    (0x21, c | s, w, ShortcutAction.PreviousWorkspace),
    (0x22, c | s, w, ShortcutAction.NextWorkspace),
    (0x26, c | a | s, w, ShortcutAction.MoveWorkspaceUp),
    (0x28, c | a | s, w, ShortcutAction.MoveWorkspaceDown),
    (0x71, c | a | s, w, ShortcutAction.RenameWorkspace),
    ('W', c | a | s, w, ShortcutAction.CloseWorkspace),
    ('N', c | a, w, ShortcutAction.NewScreen),
    (0x21, c | a, w, ShortcutAction.PreviousScreen),
    (0x22, c | a, w, ShortcutAction.NextScreen),
    (0x71, c | a, w, ShortcutAction.RenameScreen),
    ('W', c | a, w, ShortcutAction.CloseScreen),
    (0xDC, c | s, w, ShortcutAction.SplitPaneRight),
    (0xBD, c | s, w, ShortcutAction.SplitPaneDown),
    (0x25, c | a, w, ShortcutAction.FocusPaneLeft),
    (0x27, c | a, w, ShortcutAction.FocusPaneRight),
    (0x26, c | a, w, ShortcutAction.FocusPaneUp),
    (0x28, c | a, w, ShortcutAction.FocusPaneDown),
    (0x0D, c | s, w, ShortcutAction.TogglePaneZoom),
    (0x71, c | s, w, ShortcutAction.RenamePane),
    ('W', c | s, w, ShortcutAction.ClosePane),
    ('T', c, w, ShortcutAction.NewTerminalTab),
    ('T', c | s, w, ShortcutAction.NewBrowserTab),
    (0x09, c | s, w, ShortcutAction.PreviousTab),
    (0x09, c, w, ShortcutAction.NextTab),
    (0x25, c | a | s, w, ShortcutAction.MoveTabLeft),
    (0x27, c | a | s, w, ShortcutAction.MoveTabRight),
    (0x71, c, w, ShortcutAction.RenameTab),
    ('W', c, w, ShortcutAction.CloseTab),
    (0x25, a, b, ShortcutAction.BrowserBack),
    (0x27, a, b, ShortcutAction.BrowserForward),
    ('R', c, b, ShortcutAction.BrowserReload),
    (0x74, ShortcutModifiers.None, b, ShortcutAction.BrowserReload),
    ('L', c, b, ShortcutAction.BrowserFocusAddress),
    ('C', c | s, w, ShortcutAction.TerminalCopy),
    (0x2D, c, w, ShortcutAction.TerminalCopy),
    ('V', c, w, ShortcutAction.TerminalPaste),
    ('V', c | s, w, ShortcutAction.TerminalPaste),
    (0x2D, s, w, ShortcutAction.TerminalPaste),
    ('A', c | s, w, ShortcutAction.TerminalSelectAll),
};

foreach (var binding in expected)
{
    Match(binding.Key, binding.Modifiers, binding.Context, binding.Action);
    if (binding.Context == w
        && binding.Action is not ShortcutAction.TerminalCopy
            and not ShortcutAction.TerminalPaste
            and not ShortcutAction.TerminalSelectAll)
    {
        Match(binding.Key, binding.Modifiers, b, binding.Action);
    }
}

var allActions = Enum.GetValues<ShortcutAction>();
var coveredActions = expected.Select(binding => binding.Action)
    .Append(ShortcutAction.SelectWorkspace)
    .Append(ShortcutAction.SelectScreen)
    .Append(ShortcutAction.SelectTab)
    .Append(ShortcutAction.MoveTabToPane)
    .Distinct()
    .ToHashSet();
Check(allActions.All(coveredActions.Contains), "The expectation table does not cover every action");
Check(allActions.All(ShortcutDefinitions.DefinedActions().Contains), "The catalog does not define every action");

Check(ShortcutDefinitions.Match(new(0x25, a), w) is null,
    "Alt+Left must remain terminal-owned");
Check(ShortcutDefinitions.Match(new('R', c), w) is null,
    "Ctrl+R must remain terminal-owned");
Check(ShortcutDefinitions.Match(new('N', c | s | a), w) is null,
    "Extra modifiers must not match Ctrl+Shift+N");

foreach (var context in new[] { w, b })
{
    foreach (var key in Enumerable.Range(0, 256))
    {
        for (var modifierBits = 0; modifierBits <= 7; modifierBits++)
        {
            var modifiers = (ShortcutModifiers)modifierBits;
            Check(ShortcutDefinitions.Match(new(key, modifiers, true), context) is null,
                "AltGr must reject every application shortcut");
        }
    }
}

for (var digit = '1'; digit <= '9'; digit++)
{
    var expectedIndex = digit - '1';
    Match(digit, c, w, ShortcutAction.SelectTab, expectedIndex);
    Match(digit, c | a, w, ShortcutAction.SelectScreen, expectedIndex);
    Match(digit, c | s, w, ShortcutAction.SelectWorkspace, expectedIndex);
    Match(digit, c | a | s, w, ShortcutAction.MoveTabToPane, expectedIndex);
}
Match('0', c, w, ShortcutAction.SelectTab, 9);
Match('0', c | a, w, ShortcutAction.SelectScreen, 9);
Match('0', c | s, w, ShortcutAction.SelectWorkspace, 9);
Match('0', c | a | s, w, ShortcutAction.MoveTabToPane, 9);

foreach (var action in allActions.Where(action => action.ToString().StartsWith("Close", StringComparison.Ordinal)))
{
    var matches = new List<ShortcutMatch>();
    foreach (var context in new[] { w, b })
    {
        foreach (var key in Enumerable.Range(0, 256))
        {
            for (var modifierBits = 0; modifierBits <= 7; modifierBits++)
            {
                if (ShortcutDefinitions.Match(
                        new(key, (ShortcutModifiers)modifierBits), context) is { } match
                    && match.Action == action)
                {
                    matches.Add(match);
                }
            }
        }
    }
    Check(matches.Count > 0 && matches.All(match => match.Destructive),
        $"{action} must be protected and marked destructive");
}

Console.WriteLine($"Validated {expected.Length} bindings for {allActions.Length} shortcut actions.");
