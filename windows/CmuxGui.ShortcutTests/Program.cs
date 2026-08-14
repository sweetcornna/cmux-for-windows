using System.Diagnostics;
using System.Xml.Linq;
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

static void ValidateProviderLauncher()
{
    var shimDirectory = Path.Combine(AppContext.BaseDirectory, "AgentIntegration", "bin");
    foreach (var provider in new[] { "claude", "opencode", "codex" })
    {
        var shim = File.ReadAllText(Path.Combine(shimDirectory, $"{provider}.cmd"));
        Check(!shim.Contains($" {provider} -- %*", StringComparison.Ordinal),
            $"{provider} shim must not pass a bare PowerShell separator");
    }

    var root = Path.Combine(Path.GetTempPath(), $"cmux-provider-launcher-{Guid.NewGuid():N}");
    var providerDirectory = Path.Combine(root, "providers");
    Directory.CreateDirectory(providerDirectory);
    try
    {
        var capture = Path.Combine(root, "arguments.txt");
        File.WriteAllText(Path.Combine(providerDirectory, "claude.cmd"),
            "@echo off\r\n> \"%CMUX_PROVIDER_CAPTURE%\" (\r\n  for %%A in (%*) do echo(%%~A\r\n)\r\n");
        var runner = Path.Combine(root, "run.cmd");
        File.WriteAllText(runner,
            $"@call \"{Path.Combine(shimDirectory, "claude.cmd")}\" -p \"two words\" -- --version\r\n@exit /b %errorlevel%\r\n");

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(runner);
        start.Environment["PATH"] = string.Join(Path.PathSeparator,
            shimDirectory,
            providerDirectory,
            Environment.GetEnvironmentVariable("PATH"));
        start.Environment["CMUX_PROVIDER_CAPTURE"] = capture;
        start.Environment.Remove("CMUX_AGENT_INTEGRATION_DIR");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Provider launcher test process did not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Check(process.WaitForExit(10_000), "Provider launcher test timed out");
        Check(process.ExitCode == 0, $"Provider launcher failed: {output}{error}");
        Check(File.ReadAllLines(capture).SequenceEqual(new[] { "-p", "two words", "--", "--version" }),
            "Provider launcher must preserve short options, quoted values, and separators");
    }
    finally
    {
        Directory.Delete(root, true);
    }
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

var mainWindow = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
static XElement Named(XDocument document, string name) =>
    document.Descendants().Single(element => element.Attributes().Any(attribute =>
        attribute.Name.LocalName == "Name" && attribute.Value == name));

var notification = Named(mainWindow, "AgentNotificationCard");
Check(notification.Name.LocalName == "Border", "Agent notifications must not use a focus-navigating control");
foreach (var name in new[] { "AgentNotificationFocusButton", "AgentNotificationCloseButton" })
{
    var button = Named(mainWindow, name);
    Check((string?)button.Attribute("IsTabStop") == "False", $"{name} must not enter tab focus");
    Check((string?)button.Attribute("AllowFocusOnInteraction") == "False",
        $"{name} must not steal terminal focus on interaction");
}

var unicode = new UnicodeInputDecoder();
Check(unicode.DecodeUtf16Unit('A') == "A", "ASCII UTF-16 input must be preserved");
Check(unicode.DecodeUtf16Unit('é') == "é", "accented UTF-16 input must be preserved");
Check(unicode.DecodeUtf16Unit('中') == "中", "BMP CJK UTF-16 input must be preserved");
Check(unicode.DecodeUtf16Unit('\u0301') == "\u0301", "combining marks must be preserved");
Check(unicode.DecodeUtf16Unit('\u200D') == "\u200D", "zero-width joiners must be preserved");
Check(unicode.DecodeUtf16Unit('\uFE0F') == "\uFE0F", "variation selectors must be preserved");
Check(unicode.DecodeUtf16Unit('\uD83D').Length == 0, "a high surrogate must wait for its pair");
Check(unicode.DecodeUtf16Unit('\uDE00') == "😀", "an emoji surrogate pair must stay intact");
Check(unicode.DecodeUtf16Unit('\uDC00').Length == 0, "an isolated low surrogate must be rejected");
unicode.DecodeUtf16Unit('\uD840');
unicode.Reset();
Check(unicode.DecodeUtf16Unit('x') == "x", "reset must discard an incomplete surrogate pair");
Check(UnicodeInputDecoder.DecodeScalar(0x1F642) == "🙂", "WM_UNICHAR emoji must be preserved");
Check(UnicodeInputDecoder.DecodeScalar(0x20000) == "𠀀", "supplementary CJK must be preserved");
Check(UnicodeInputDecoder.DecodeScalar(0xD800).Length == 0, "surrogate scalar values must be rejected");
Check(UnicodeInputDecoder.DecodeScalar(0x110000).Length == 0, "out-of-range Unicode must be rejected");

var wheel = new WheelDeltaAccumulator();
Check(wheel.Add(30) == 0, "partial wheel input must wait for a complete notch");
Check(wheel.Add(30) == 0, "partial wheel input must be retained");
Check(wheel.Add(30) == 0, "partial wheel input must not scroll early");
Check(wheel.Add(30) == -3, "four quarter-notch wheel inputs must scroll toward older output");
Check(wheel.Add(-60) == 0, "reverse partial wheel input must be retained");
Check(wheel.Add(-60) == 3, "two reverse half-notches must scroll toward newer output");
Check(wheel.Add(240) == -6, "multiple wheel notches must preserve their row count");

var cmuxFfi = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CmuxFfi.rs"));
Check(cmuxFfi.Contains("surface.render_view_frame(&mut session.render)", StringComparison.Ordinal),
    "GUI snapshots must render the terminal view's local scrollback offset");
ValidateProviderLauncher();

var terminalView = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TerminalView.cs"));
var characterHandlerStart = terminalView.IndexOf("private bool HandleCharacterReceived", StringComparison.Ordinal);
var keyHandlerStart = terminalView.IndexOf("private void OnKeyDown", characterHandlerStart, StringComparison.Ordinal);
Check(characterHandlerStart >= 0 && keyHandlerStart > characterHandlerStart,
    "Terminal character handler could not be located");
var characterHandler = terminalView[characterHandlerStart..keyHandlerStart];
Check(!characterHandler.Contains("_textInputHasFocus", StringComparison.Ordinal),
    "CoreText focus must not suppress direct keyboard-layout characters");
Check(characterHandler.Contains("Send(Encoding.UTF8.GetBytes(text))", StringComparison.Ordinal),
    "Direct keyboard-layout characters must reach the terminal");

Console.WriteLine(
    $"Validated {expected.Length} bindings for {allActions.Length} shortcut actions, notification focus isolation, full Unicode character input, and precision wheel scrolling.");
