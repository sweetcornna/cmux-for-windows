using System;
using System.Text;
using System.Threading.Tasks;
using CmuxGui.Interop;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CmuxGui.Controls;

/// <summary>
/// Mouse handling: selection, clipboard, and scrollback.
///
/// Selection and copy need no engine support. The control already holds the
/// whole cell grid for rendering, so the text is right there; only scrolling
/// has to reach the engine, because the scrollback buffer lives there.
/// </summary>
public sealed partial class TerminalView
{
    // Tracked in grid cells rather than pixels, so it survives resizes and maps
    // straight onto the cell array when extracting text.
    private (int Col, int Row)? _selectionAnchor;
    private (int Col, int Row)? _selectionFocus;
    private bool _selecting;
    private bool _terminalMouseForwarding;
    private byte _terminalMouseButton;
    private DateTimeOffset _lastClickAt;
    private (int Col, int Row) _lastClickCell;
    private int _clickCount;

    private bool HasSelection => _selectionAnchor is not null && _selectionFocus is not null;

    private void BuildContextMenu()
    {
        var copy = new MenuFlyoutItem { Text = Loc.S("Menu_Copy") };
        copy.Click += (_, _) => CopySelection();

        var paste = new MenuFlyoutItem { Text = Loc.S("Menu_Paste") };
        paste.Click += async (_, _) => await PasteAsync();

        var selectAll = new MenuFlyoutItem { Text = Loc.S("Menu_SelectAll") };
        selectAll.Click += (_, _) => SelectAll();

        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAll);
        // Reflect reality on open: Copy is meaningless with nothing selected.
        menu.Opening += (_, _) => copy.IsEnabled = HasSelection;
        ContextFlyout = menu;
    }

    /// <summary>Pointer position as a grid cell, clamped into the grid.</summary>
    private (int Col, int Row) CellAt(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
        var col = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, _frame.Cols - 1));
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, _frame.Rows - 1));
        return (col, row);
    }

    private static byte MouseModifiers()
    {
        var modifiers = (byte)0;
        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= 1;
        }
        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= 2;
        }
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= 4;
        }
        return modifiers;
    }

    private void OnPointerDown(object sender, PointerRoutedEventArgs e)
    {
        TakeFocus("pointer", FocusState.Pointer);
        var point = e.GetCurrentPoint(_canvas);
        var button = point.Properties.IsLeftButtonPressed ? (byte)1
            : point.Properties.IsMiddleButtonPressed ? (byte)2
            : point.Properties.IsRightButtonPressed ? (byte)3
            : (byte)0;
        var cell = CellAt(e);
        if (_session != IntPtr.Zero && !IsKeyDown(VirtualKey.Shift)
            && CmuxNative.SessionMouse(
                _session,
                0,
                (ushort)cell.Col,
                (ushort)cell.Row,
                button,
                MouseModifiers(),
                0) == 1)
        {
            _terminalMouseForwarding = true;
            _terminalMouseButton = button;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (button == 3)
        {
            return;
        }

        _selecting = true;
        if (IsKeyDown(VirtualKey.Shift) && _selectionAnchor is not null)
        {
            _selectionFocus = cell;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            _clickCount = cell == _lastClickCell && now - _lastClickAt < TimeSpan.FromMilliseconds(500)
                ? Math.Min(3, _clickCount + 1)
                : 1;
            _lastClickAt = now;
            _lastClickCell = cell;
            if (_clickCount == 2)
            {
                SelectWordAt(cell);
                _selecting = false;
            }
            else if (_clickCount == 3)
            {
                _selectionAnchor = (0, cell.Row);
                _selectionFocus = (Math.Max(0, _frame.Cols - 1), cell.Row);
                _selecting = false;
            }
            else
            {
                _selectionAnchor = cell;
                _selectionFocus = cell;
            }
        }
        if (_selecting)
        {
            CapturePointer(e.Pointer);
        }
        _canvas.Invalidate();
    }

    private void OnPointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (_terminalMouseForwarding && _session != IntPtr.Zero)
        {
            var cell = CellAt(e);
            CmuxNative.SessionMouse(
                _session,
                2,
                (ushort)cell.Col,
                (ushort)cell.Row,
                _terminalMouseButton,
                MouseModifiers(),
                0);
            e.Handled = true;
            return;
        }
        if (!_selecting)
        {
            return;
        }
        var position = e.GetCurrentPoint(_canvas).Position;
        if (_session != IntPtr.Zero && position.Y < 0)
        {
            CmuxNative.SessionScroll(_session, -3);
        }
        else if (_session != IntPtr.Zero && position.Y > _canvas.ActualHeight)
        {
            CmuxNative.SessionScroll(_session, 3);
        }
        _selectionFocus = CellAt(e);
        _canvas.Invalidate();
    }

    private void OnPointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (_terminalMouseForwarding)
        {
            var cell = CellAt(e);
            if (_session != IntPtr.Zero)
            {
                CmuxNative.SessionMouse(
                    _session,
                    1,
                    (ushort)cell.Col,
                    (ushort)cell.Row,
                    _terminalMouseButton,
                    MouseModifiers(),
                    0);
            }
            _terminalMouseForwarding = false;
            _terminalMouseButton = 0;
            ReleasePointerCapture(e.Pointer);
            TakeFocus("pointer-up", FocusState.Pointer);
            e.Handled = true;
            return;
        }
        if (!_selecting)
        {
            return;
        }
        _selecting = false;
        ReleasePointerCapture(e.Pointer);

        // A click without a drag is a click, not an empty selection.
        if (_selectionAnchor == _selectionFocus)
        {
            _selectionAnchor = null;
            _selectionFocus = null;
            _canvas.Invalidate();
        }

        // Releasing the button hands focus to an ancestor ScrollViewer, which
        // is not cancelable, so the only way to keep it is to take it back
        // afterwards. Without this a click leaves the terminal with no focus at
        // all and every following keystroke is discarded -- and clicking the
        // terminal is the first thing anyone does before typing.
        TakeFocus("pointer-up", FocusState.Pointer);
        // The transfer can also land after this handler returns, so assert once
        // more when the focus change has settled. Scoped to a press that began
        // on the terminal, so it never fights a click on the sidebar.
        DispatcherQueue.TryEnqueue(() => TakeFocus("pointer-settled", FocusState.Pointer));
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        var delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        var rows = -(delta / 120) * 3;
        if (rows == 0)
        {
            return;
        }
        var cell = CellAt(e);
        if (!IsKeyDown(VirtualKey.Shift)
            && CmuxNative.SessionMouse(
                _session,
                3,
                (ushort)cell.Col,
                (ushort)cell.Row,
                0,
                MouseModifiers(),
                rows) == 1)
        {
            e.Handled = true;
            return;
        }
        CmuxNative.SessionScroll(_session, rows);
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void SelectAll()
    {
        if (_frame.Cols == 0 || _frame.Rows == 0)
        {
            return;
        }
        _selectionAnchor = (0, 0);
        _selectionFocus = (_frame.Cols - 1, _frame.Rows - 1);
        _canvas.Invalidate();
    }

    /// <summary>Selection bounds in reading order, since a drag may run backwards.</summary>
    internal ((int Col, int Row) Start, (int Col, int Row) End)? SelectionRange()
    {
        if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus)
        {
            return null;
        }
        var forward = focus.Row > anchor.Row || (focus.Row == anchor.Row && focus.Col >= anchor.Col);
        return forward ? (anchor, focus) : (focus, anchor);
    }

    private void SelectWordAt((int Col, int Row) cell)
    {
        if (_frame.Cols == 0 || cell.Row >= _frame.Rows)
        {
            return;
        }
        var start = cell.Col;
        var end = cell.Col;
        var word = IsWordCell(cell.Col, cell.Row);
        while (start > 0 && IsWordCell(start - 1, cell.Row) == word)
        {
            start--;
        }
        while (end + 1 < _frame.Cols && IsWordCell(end + 1, cell.Row) == word)
        {
            end++;
        }
        _selectionAnchor = (start, cell.Row);
        _selectionFocus = (end, cell.Row);
    }

    private bool IsWordCell(int col, int row)
    {
        var index = row * _frame.Cols + col;
        if (index < 0 || index >= _cellCount)
        {
            return false;
        }
        var text = CmuxNative.TextOf(_cells[index]);
        return text.Length > 0 && (char.IsLetterOrDigit(text, 0) || text[0] == '_');
    }

    private string SelectedText()
    {
        if (SelectionRange() is not { } range || _cellCount == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (var row = range.Start.Row; row <= range.End.Row; row++)
        {
            var first = row == range.Start.Row ? range.Start.Col : 0;
            var last = row == range.End.Row ? range.End.Col : _frame.Cols - 1;

            var line = new StringBuilder();
            for (var col = first; col <= last; col++)
            {
                var index = row * _frame.Cols + col;
                if (index >= _cellCount)
                {
                    break;
                }
                var cell = _cells[index];
                if (cell.Width == 0)
                {
                    // Trailing half of a wide glyph; the lead already supplied it.
                    continue;
                }
                var grapheme = CmuxNative.TextOf(cell);
                line.Append(cell.Ch == 0 ? " " : grapheme.Length == 0
                    ? char.ConvertFromUtf32((int)cell.Ch)
                    : grapheme);
            }

            // Rows are space-padded to the full width, and keeping that padding
            // would paste a wall of trailing spaces.
            text.Append(line.ToString().TrimEnd());
            var rowStart = row * _frame.Cols;
            var softWrapped = rowStart < _cellCount && (_cells[rowStart].RowFlags & 1) != 0;
            if (row != range.End.Row && !softWrapped)
            {
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async Task PasteAsync()
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                return;
            }
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
            CmuxNative.SessionPaste(_session, bytes, (nuint)bytes.Length);
        }
        catch (Exception ex)
        {
            Diag.Log($"paste failed: {ex.Message}");
        }
    }
}
