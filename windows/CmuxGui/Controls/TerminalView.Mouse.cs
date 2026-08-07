using System;
using System.Text;
using System.Threading.Tasks;
using CmuxGui.Interop;
using CmuxGui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

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

    private void OnPointerDown(object sender, PointerRoutedEventArgs e)
    {
        TakeFocus("pointer", FocusState.Pointer);

        if (e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed)
        {
            // Leave the selection alone; the context menu acts on it.
            return;
        }

        _selecting = true;
        _selectionAnchor = CellAt(e);
        _selectionFocus = _selectionAnchor;
        CapturePointer(e.Pointer);
        _canvas.Invalidate();
    }

    private void OnPointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }
        _selectionFocus = CellAt(e);
        _canvas.Invalidate();
    }

    private void OnPointerUp(object sender, PointerRoutedEventArgs e)
    {
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
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        // A notch is 120 units, and Windows scrolls three lines per notch.
        var delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        var rows = -(delta / 120) * 3;
        if (rows != 0)
        {
            CmuxNative.SessionScroll(_session, rows);
            _canvas.Invalidate();
        }
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
                line.Append(cell.Ch == 0 ? " " : char.ConvertFromUtf32((int)cell.Ch));
            }

            // Rows are space-padded to the full width, and keeping that padding
            // would paste a wall of trailing spaces.
            text.Append(line.ToString().TrimEnd());
            if (row != range.End.Row)
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
            // A terminal takes CR for Enter; pasting CRLF would submit twice.
            Send(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\r").Replace("\n", "\r")));
        }
        catch (Exception ex)
        {
            Diag.Log($"paste failed: {ex.Message}");
        }
    }
}
