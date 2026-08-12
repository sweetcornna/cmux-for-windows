using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace CmuxGui.Input;

internal static class ShortcutKeyState
{
    internal static ShortcutModifiers CurrentModifiers()
    {
        var modifiers = ShortcutModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= ShortcutModifiers.Control;
        }
        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= ShortcutModifiers.Alt;
        }
        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= ShortcutModifiers.Shift;
        }
        return modifiers;
    }

    internal static bool IsAltGr() =>
        IsDown(VirtualKey.RightMenu) && IsDown(VirtualKey.Control);

    internal static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
