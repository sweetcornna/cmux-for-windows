using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI.Text.Core;

namespace CmuxGui.Controls;

public sealed partial class TerminalView
{
    private CoreTextEditContext? _textInputContext;
    private string _textInputBuffer = string.Empty;
    private CoreTextRange _textInputSelection;
    private bool _textInputComposing;
    private bool _textInputHasFocus;

    private void InitializeTextInput()
    {
        try
        {
            _textInputContext = CoreTextServicesManager.GetForCurrentView().CreateEditContext();
            _textInputContext.InputScope = CoreTextInputScope.Text;
            _textInputContext.TextRequested += OnTextRequested;
            _textInputContext.SelectionRequested += OnTextSelectionRequested;
            _textInputContext.TextUpdating += OnTextUpdating;
            _textInputContext.SelectionUpdating += OnTextSelectionUpdating;
            _textInputContext.CompositionStarted += OnTextCompositionStarted;
            _textInputContext.CompositionCompleted += OnTextCompositionCompleted;
            _textInputContext.LayoutRequested += OnTextLayoutRequested;
            _textInputContext.FocusRemoved += OnTextFocusRemoved;
        }
        catch (Exception ex)
        {
            _textInputContext = null;
            Diag.Log($"terminal text input initialization failed: {ex.Message}");
        }
    }

    private void OnTextInputGotFocus(object sender, RoutedEventArgs args) =>
        EnterTextInputFocus();

    private void OnTextInputLostFocus(object sender, RoutedEventArgs args) =>
        LeaveTextInputFocus();

    private void EnterTextInputFocus()
    {
        if (_textInputContext is null
            || _textInputHasFocus
            || !_hostActive
            || FocusState == FocusState.Unfocused)
        {
            return;
        }
        try
        {
            _textInputContext.NotifyFocusEnter();
            _textInputHasFocus = true;
        }
        catch (Exception ex)
        {
            Diag.Log($"terminal text input focus failed: {ex.Message}");
        }
    }

    private void LeaveTextInputFocus()
    {
        if (_textInputContext is not null && _textInputHasFocus)
        {
            try
            {
                _textInputContext.NotifyFocusLeave();
            }
            catch (Exception ex)
            {
                Diag.Log($"terminal text input blur failed: {ex.Message}");
            }
        }
        ResetTextInput();
    }

    private void OnTextFocusRemoved(CoreTextEditContext sender, object args) =>
        ResetTextInput();

    private void OnTextRequested(
        CoreTextEditContext sender,
        CoreTextTextRequestedEventArgs args)
    {
        var range = args.Request.Range;
        var start = Math.Clamp(range.StartCaretPosition, 0, _textInputBuffer.Length);
        var end = Math.Clamp(range.EndCaretPosition, start, _textInputBuffer.Length);
        args.Request.Text = _textInputBuffer[start..end];
    }

    private void OnTextSelectionRequested(
        CoreTextEditContext sender,
        CoreTextSelectionRequestedEventArgs args) =>
        args.Request.Selection = _textInputSelection;

    private void OnTextUpdating(
        CoreTextEditContext sender,
        CoreTextTextUpdatingEventArgs args)
    {
        var start = Math.Clamp(args.Range.StartCaretPosition, 0, _textInputBuffer.Length);
        var end = Math.Clamp(args.Range.EndCaretPosition, start, _textInputBuffer.Length);
        _textInputBuffer = _textInputBuffer[..start] + args.Text + _textInputBuffer[end..];
        _textInputSelection = ClampTextRange(args.NewSelection, _textInputBuffer.Length);
        args.Result = CoreTextTextUpdatingResult.Succeeded;

        if (!_textInputComposing)
        {
            CommitTextInput();
        }
    }

    private void OnTextSelectionUpdating(
        CoreTextEditContext sender,
        CoreTextSelectionUpdatingEventArgs args)
    {
        _textInputSelection = ClampTextRange(args.Selection, _textInputBuffer.Length);
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
    }

    private void OnTextCompositionStarted(
        CoreTextEditContext sender,
        CoreTextCompositionStartedEventArgs args) =>
        _textInputComposing = true;

    private void OnTextCompositionCompleted(
        CoreTextEditContext sender,
        CoreTextCompositionCompletedEventArgs args)
    {
        _textInputComposing = false;
        CommitTextInput();
    }

    private void CommitTextInput()
    {
        var text = _textInputBuffer;
        _textInputBuffer = string.Empty;
        _textInputSelection = CreateTextRange(0, 0);

        if (_hostActive && text.Length > 0)
        {
            Send(Encoding.UTF8.GetBytes(text));
        }
        if (text.Length > 0 && _textInputContext is { } context)
        {
            DispatcherQueue.TryEnqueue(() => NotifyTextInputCleared(context, text.Length));
        }
    }

    private void NotifyTextInputCleared(CoreTextEditContext context, int replacedLength)
    {
        if (_disposed
            || !_textInputHasFocus
            || !ReferenceEquals(_textInputContext, context))
        {
            return;
        }
        try
        {
            context.NotifyTextChanged(
                CreateTextRange(0, replacedLength),
                0,
                _textInputSelection);
        }
        catch (Exception ex)
        {
            Diag.Log($"terminal text input reset failed: {ex.Message}");
        }
    }

    private void ResetTextInput()
    {
        _textInputHasFocus = false;
        _textInputComposing = false;
        _textInputBuffer = string.Empty;
        _textInputSelection = CreateTextRange(0, 0);
    }

    private void OnTextLayoutRequested(
        CoreTextEditContext sender,
        CoreTextLayoutRequestedEventArgs args)
    {
        if (App.MainWindowHandle == IntPtr.Zero || XamlRoot is null)
        {
            return;
        }
        try
        {
            var origin = new NativePoint();
            if (!ClientToScreen(App.MainWindowHandle, ref origin))
            {
                return;
            }

            var scale = XamlRoot.RasterizationScale;
            var canvasBounds = _canvas.TransformToVisual(null).TransformBounds(
                new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
            var cursor = _canvas.TransformToVisual(null).TransformPoint(
                new Point(_frame.CursorCol * _cellWidth, _frame.CursorRow * _cellHeight));
            args.Request.LayoutBounds.ControlBounds = ScaleToScreen(canvasBounds, origin, scale);
            args.Request.LayoutBounds.TextBounds = new Rect(
                origin.X + cursor.X * scale,
                origin.Y + cursor.Y * scale,
                Math.Max(1, _cellWidth * scale),
                Math.Max(1, _cellHeight * scale));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Rect ScaleToScreen(Rect bounds, NativePoint origin, double scale) =>
        new(
            origin.X + bounds.X * scale,
            origin.Y + bounds.Y * scale,
            Math.Max(1, bounds.Width * scale),
            Math.Max(1, bounds.Height * scale));

    private static CoreTextRange ClampTextRange(CoreTextRange range, int length) =>
        CreateTextRange(
            Math.Clamp(range.StartCaretPosition, 0, length),
            Math.Clamp(range.EndCaretPosition, 0, length));

    private static CoreTextRange CreateTextRange(int start, int end) => new()
    {
        StartCaretPosition = start,
        EndCaretPosition = end,
    };

    private void DisposeTextInput()
    {
        LeaveTextInputFocus();
        if (_textInputContext is null)
        {
            return;
        }
        _textInputContext.TextRequested -= OnTextRequested;
        _textInputContext.SelectionRequested -= OnTextSelectionRequested;
        _textInputContext.TextUpdating -= OnTextUpdating;
        _textInputContext.SelectionUpdating -= OnTextSelectionUpdating;
        _textInputContext.CompositionStarted -= OnTextCompositionStarted;
        _textInputContext.CompositionCompleted -= OnTextCompositionCompleted;
        _textInputContext.LayoutRequested -= OnTextLayoutRequested;
        _textInputContext.FocusRemoved -= OnTextFocusRemoved;
        _textInputContext = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
