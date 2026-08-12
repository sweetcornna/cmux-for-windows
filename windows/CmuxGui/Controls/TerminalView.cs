using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CmuxGui.Input;
using CmuxGui.Interop;
using CmuxGui.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace CmuxGui.Controls;

/// <summary>
/// A terminal surface backed by the cmux engine and drawn with Win2D.
///
/// Rendering lives on a <see cref="CanvasControl"/> rather than XAML text
/// elements: a terminal is a dense uniform grid that changes every frame, which
/// is the case XAML's layout system is worst at and an immediate-mode canvas is
/// best at. This mirrors how Windows Terminal hosts its own renderer.
/// </summary>
public sealed partial class TerminalView : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new();
    private readonly DispatcherTimer _timer = new();
    // The image sits under a transparent canvas, so terminal opacity reveals it
    // rather than the canvas having to composite the bitmap itself.
    private readonly Grid _root = new();
    private readonly Image _backgroundImage = new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    /// <summary>Scrim between the image and the grid, so text keeps its contrast.</summary>
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _mask = new();

    private readonly MuxRuntime _mux;
    private readonly string _tabId;
    private IntPtr _session;
    private CmuxNative.Cell[] _cells = Array.Empty<CmuxNative.Cell>();
    private CmuxNative.Frame _frame;
    private int _cellCount;

    private CanvasTextFormat? _format;
    private readonly CanvasTextFormat?[] _formats = new CanvasTextFormat?[4];
    private readonly Dictionary<GlyphLayoutKey, CanvasTextLayout> _glyphLayouts = [];
    private readonly Queue<GlyphLayoutKey> _glyphLayoutOrder = [];
    private string _status = string.Empty;
    // Used until the first snapshot arrives, so the very first paint already
    // shows the Ghostty background instead of flashing a default.
    private uint _themeBackground = CmuxNative.NoColor;
    private uint _themeForeground = CmuxNative.NoColor;
    private uint _selectionBackground = CmuxNative.NoColor;
    private uint _selectionForeground = CmuxNative.NoColor;
    private float _cellWidth = 8;
    private float _cellHeight = 16;
    private bool _hasBlinkingText;
    private bool _blinkVisible = true;
    private const int GlyphLayoutCapacity = 4096;
    // The starting cell size above is a placeholder. Sizing the PTY from it
    // would start the shell on a grid the font does not actually produce, and
    // text it has already emitted keeps that wrong wrapping forever.
    private bool _metricsReady;
    private ushort _cols;
    private ushort _rows;
    /// <summary>Input typed before the session existed. See <see cref="Send"/>.</summary>
    private readonly List<byte> _pending = new();
    private readonly List<(string Chord, byte Action)> _pendingKeys = [];
    private readonly Dictionary<(VirtualKey Key, uint ScanCode, bool Extended), string>
        _structuredKeysDown = [];
    private bool _suppressStructuredCharacter;
    private bool _postArrangeRefreshPending;
    private bool _canvasAttached;
    private bool _hostActive;
    private bool _disposed;
    private int _loadGeneration;

    internal TerminalView(MuxRuntime mux, string tabId)
    {
        _mux = mux;
        _tabId = tabId;
        _root.Children.Add(_backgroundImage);
        _root.Children.Add(_mask);
        // A rounded, clipped surface is what makes terminal content read as a
        // Windows 11 card instead of a raw rectangle butted against the chrome.
        // Border clips its child to the corner radius; Grid cannot.
        // TabView.Padding does not reach the content presenter, so the inset
        // has to live on the card itself. The hairline stroke is what makes the
        // corner radius legible against a dark terminal.
        Content = new Border
        {
            Child = _root,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(2, 0, 8, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
        };
        IsTabStop = true;
        // No focus rectangle. A terminal signals focus with its cursor, and the
        // system visual draws a bright frame around the whole grid the moment
        // focus is taken programmatically at startup.
        UseSystemFocusVisuals = false;

        // The canvas is a rendering surface, not a control. CanvasControl
        // derives from Control, so by default a click lands on it and it takes
        // focus away from this view -- about 80ms after the press, when the
        // button comes back up. The terminal then held no focus and every
        // keystroke went nowhere, which looked like a dead window.
        _canvas.IsTabStop = false;
        _canvas.AllowFocusOnInteraction = false;
        _backgroundImage.AllowFocusOnInteraction = false;
        _mask.AllowFocusOnInteraction = false;

        // Transparent clear lets the terminal background carry an alpha and
        // composite over the image and the window backdrop beneath it.
        _canvas.ClearColor = Colors.Transparent;
        AppSettings.Changed += OnSettingsChanged;

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += OnCreateResources;
        _canvas.SizeChanged += OnCanvasSizeChanged;

        // The engine has no GUI wakeup yet, so poll near display rate. Redraws
        // are skipped unless the snapshot reports damage.
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            var blinkVisible = (Environment.TickCount64 / 500) % 2 == 0;
            if (blinkVisible != _blinkVisible
                && (_hasBlinkingText || (_frame.CursorVisible != 0 && _frame.CursorBlink != 0)))
            {
                _blinkVisible = blinkVisible;
                _canvas.Invalidate();
            }
            Poll();
        };

        // A click has to move focus here or every keystroke goes to whatever
        // the shell focused first (the search box). CanvasControl can mark
        // pointer events handled, so handledEventsToo is required.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerDown), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMove), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerUp), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), true);
        BuildContextMenu();

        Diag.Log("TerminalView ctor");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Deliberately NOT disposing on Unloaded: TabView unloads and reloads
        // its content during setup and on every tab switch, so tearing the
        // session down there kills the terminal before it ever draws. The
        // owning tab disposes this explicitly when it is closed.
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        CharacterReceived += OnCharacterReceived;
        // Focus moving away silently is one of the few remaining explanations
        // for keystrokes that never arrive, so make it visible. LosingFocus
        // names the element taking over, which LostFocus cannot.
        GotFocus += (_, _) => Diag.Log("terminal got focus");
        LosingFocus += (_, e) => Diag.Log(
            $"terminal losing focus to {e.NewFocusedElement?.GetType().FullName ?? "nothing"} "
            + $"(state={e.FocusState}, direction={e.Direction}, cancelable={e.Cancel})");
        LostFocus += (_, _) =>
        {
            ResetStructuredKeyState();
            var holder = XamlRoot is null
                ? "no-xaml-root"
                : FocusManager.GetFocusedElement(XamlRoot)?.GetType().FullName ?? "nothing";
            Diag.Log($"terminal lost focus; now held by {holder}");
        };
    }

    public string TabId => _tabId;

    internal void SetHostActive(bool active)
    {
        if (_disposed)
        {
            return;
        }
        _hostActive = active;
        if (!active)
        {
            ResetStructuredKeyState();
            _timer.Stop();
            return;
        }
        if (IsLoaded)
        {
            _timer.Start();
            RequestPostArrangeRefresh();
        }
    }

    private void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        DisposeTextResources();
        CmuxNative.ThemeLoad(out var theme);
        var family = CmuxNative.FontFamilyOf(theme);
        Diag.Log($"CreateResources theme loaded={theme.Loaded} font='{family}' size={theme.FontSize}");

        _themeBackground = theme.Background;
        _themeForeground = theme.Foreground;
        _selectionBackground = theme.SelectionBackground;
        _selectionForeground = theme.SelectionForeground;

        _format = new CanvasTextFormat
        {
            FontFamily = string.IsNullOrWhiteSpace(family) ? "Cascadia Mono" : family,
            FontSize = theme.FontSize > 0 ? theme.FontSize : 14,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        for (var i = 0; i < 4; i++)
        {
            _formats[i] = new CanvasTextFormat
            {
                FontFamily = _format.FontFamily,
                FontSize = _format.FontSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                Options = CanvasDrawTextOptions.Clip | CanvasDrawTextOptions.EnableColorFont,
                FontWeight = (i & 1) != 0
                    ? Microsoft.UI.Text.FontWeights.Bold
                    : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = (i & 2) != 0
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal,
            };
        }

        using var layout = new CanvasTextLayout(sender, "MMMMMMMMMM", _format, 0, 0);
        var measuredWidth = (float)layout.LayoutBoundsIncludingTrailingWhitespace.Width / 10f;
        var measuredHeight = layout.LineMetrics.Length == 0
            ? (float)layout.LayoutBounds.Height
            : layout.LineMetrics[0].Height;
        var scale = sender.Dpi / 96f;
        var cellWidthPx = (ushort)Math.Clamp((int)Math.Round(measuredWidth * scale), 1, ushort.MaxValue);
        var cellHeightPx = (ushort)Math.Clamp((int)Math.Round(measuredHeight * scale), 1, ushort.MaxValue);
        _cellWidth = cellWidthPx / scale;
        _cellHeight = cellHeightPx / scale;
        _metricsReady = true;
        if (!_mux.SetCellPixelSize(cellWidthPx, cellHeightPx))
        {
            Diag.Log($"terminal cell pixel update failed: {cellWidthPx}x{cellHeightPx}");
        }
        SyncGrid();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        Diag.Log($"Loaded generation={_loadGeneration} size={ActualWidth}x{ActualHeight}");
        RequestPostArrangeRefresh();
        if (!_canvasAttached)
        {
            // TabView can unload unselected content before its first load event.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || _canvasAttached || !IsLoaded)
                {
                    return;
                }
                _root.Children.Add(_canvas);
                _canvasAttached = true;
                RequestPostArrangeRefresh();
            });
        }
        if (_hostActive)
        {
            _timer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var generation = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && !IsLoaded && _loadGeneration == generation)
            {
                _timer.Stop();
            }
        });
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) =>
        RequestPostArrangeRefresh();

    internal void NotifyHostReparented()
    {
        RequestPostArrangeRefresh();
        if (_hostActive && IsLoaded)
        {
            _timer.Start();
        }
    }

    private void RequestPostArrangeRefresh()
    {
        if (_disposed || _postArrangeRefreshPending)
        {
            return;
        }
        _postArrangeRefreshPending = true;
        LayoutUpdated += OnPostArrangeLayoutUpdated;
    }

    private void OnPostArrangeLayoutUpdated(object? sender, object e)
    {
        if (_disposed)
        {
            LayoutUpdated -= OnPostArrangeLayoutUpdated;
            _postArrangeRefreshPending = false;
            return;
        }
        if (!SyncGrid())
        {
            return;
        }

        LayoutUpdated -= OnPostArrangeLayoutUpdated;
        _postArrangeRefreshPending = false;
        Poll(forceInvalidate: true);
    }

    /// <summary>Give the grid keyboard focus. The owning window decides when.</summary>
    public void FocusTerminal(string reason) => TakeFocus(reason, FocusState.Programmatic);

    private void TakeFocus(string reason, FocusState state)
    {
        // Window activation can fire before this control is in the visual tree,
        // and FocusManager.GetFocusedElement(null) throws. An exception here
        // surfaces as a stowed exception that kills the app at startup.
        if (XamlRoot is null)
        {
            // Logged, not swallowed: a focus request that quietly evaporates is
            // exactly how the terminal ended up unfocused at startup.
            Diag.Log($"focus({reason}) skipped: not in the visual tree");
            return;
        }
        try
        {
            var ok = Focus(state);
            var holder = FocusManager.GetFocusedElement(XamlRoot)?.GetType().Name ?? "none";
            Diag.Log($"focus({reason}) granted={ok} holder={holder}");
        }
        catch (Exception ex)
        {
            Diag.Log($"focus({reason}) failed: {ex.Message}");
        }
    }



    /// <summary>Match the PTY grid to the control size, creating the session on first use.</summary>
    private bool SyncGrid()
    {
        // Wait for the measured cell box. Sizing the PTY from the placeholder
        // starts the shell on a grid that never matches what gets drawn.
        if (!_metricsReady)
        {
            return false;
        }

        // The canvas, not this control: cells are drawn in canvas coordinates,
        // and the card border and margin sit between the two.
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (_cellWidth <= 0 || _cellHeight <= 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var cols = (ushort)Math.Max(1, (int)(width / _cellWidth));
        var rows = (ushort)Math.Max(1, (int)(height / _cellHeight));
        if (cols == _cols && rows == _rows && _session != IntPtr.Zero)
        {
            return true;
        }

        Diag.Log($"SyncGrid {cols}x{rows} canvas={width:F0}x{height:F0} cell={_cellWidth:F2}x{_cellHeight:F2}");
        if (_session == IntPtr.Zero)
        {
            try
            {
                _session = _mux.OpenTab(_tabId, cols, rows);
                Diag.Log($"TabOpen({_tabId},{cols},{rows}) -> {_session}");
                _status = _session == IntPtr.Zero
                    ? "cmux_session_new returned null"
                    : string.Empty;
                if (_session == IntPtr.Zero)
                {
                    return false;
                }

                _cols = cols;
                _rows = rows;
                ApplySettings();
                FlushPendingInput();
            }
            catch (Exception ex)
            {
                // A failure here is otherwise invisible: the canvas just stays
                // blank and the app looks broken with no explanation.
                _status = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
        else
        {
            var result = CmuxNative.SessionResize(_session, cols, rows);
            if (result != 0)
            {
                Diag.Log($"terminal resize failed: result={result} requested={cols}x{rows}");
                return false;
            }
            _cols = cols;
            _rows = rows;
        }

        var needed = cols * rows;
        if (_cells.Length < needed)
        {
            _cells = new CmuxNative.Cell[needed];
        }
        return true;
    }

    private void OnSettingsChanged() => ApplySettings();

    /// <summary>Refresh the WinUI-owned terminal background layers.</summary>
    private void ApplySettings()
    {
        var settings = AppSettings.Current;

        _backgroundImage.Opacity = settings.BackgroundImageOpacity;
        _backgroundImage.Source = null;

        // The scrim only makes sense over an image; without one it would just
        // dim the terminal's own background for no reason.
        var hasImage = !string.IsNullOrWhiteSpace(settings.BackgroundImagePath)
            && System.IO.File.Exists(settings.BackgroundImagePath);
        _mask.Fill = hasImage
            ? new SolidColorBrush(ColorUtil.WithOpacity(
                ColorUtil.ParseOr(settings.TerminalMaskColor, Colors.Black),
                settings.TerminalMaskOpacity))
            : null;
        if (hasImage)
        {
            try
            {
                _backgroundImage.Source =
                    new BitmapImage(new Uri(settings.BackgroundImagePath));
            }
            catch (Exception ex)
            {
                // A missing or unreadable image should not blank the terminal.
                Diag.Log($"background image failed: {ex.Message}");
            }
        }

        _canvas.Invalidate();
    }

    private void Poll(bool forceInvalidate = false)
    {
        if (_disposed || !_hostActive || !IsLoaded || _session == IntPtr.Zero)
        {
            return;
        }

        var written = CmuxNative.SessionSnapshot(_session, _cells, (nuint)_cells.Length, out var frame);
        if (written < 0)
        {
            return;
        }

        _cellCount = written;
        _frame = frame;
        if (forceInvalidate || frame.Dirty != 0)
        {
            if (_mux.TryGetPresentation(out var presentation))
            {
                _selectionBackground = presentation.SelectionBackground;
                _selectionForeground = presentation.SelectionForeground;
            }
            _hasBlinkingText = false;
            for (var index = 0; index < _cellCount; index++)
            {
                if (Has(_cells[index].Attrs, AttrBlink))
                {
                    _hasBlinkingText = true;
                    break;
                }
            }
            _canvas.Invalidate();
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var background = _frame.Cols == 0 ? _themeBackground : _frame.DefaultBg;
        var opaque = FromPacked(background, Colors.Black);
        var opacity = AppSettings.Current.TerminalOpacity;
        ds.Clear(opacity >= 0.999
            ? opaque
            : Color.FromArgb((byte)Math.Clamp(opacity * 255.0, 0, 255), opaque.R, opaque.G, opaque.B));

        if (_cellCount == 0 || _frame.Cols == 0 || _format is null)
        {
            if (_format is not null)
            {
                var why = string.IsNullOrEmpty(_status)
                    ? $"waiting for first frame (session={(_session == IntPtr.Zero ? "null" : "ok")}, "
                      + $"grid={_cols}x{_rows}, cell={_cellWidth:F1}x{_cellHeight:F1}, "
                      + $"size={ActualWidth:F0}x{ActualHeight:F0})"
                    : _status;
                ds.DrawText(why, 12, 12, Colors.OrangeRed, _format);
            }
            return;
        }

        var defaultFg = FromPacked(_frame.DefaultFg, FromPacked(_themeForeground, Colors.White));
        var defaultBg = FromPacked(_frame.DefaultBg, opaque);
        var selection = SelectionRange();
        var cursorSpan = CursorSpan();

        for (var row = 0; row < _frame.Rows; row++)
        {
            var y = row * _cellHeight;
            var col = 0;
            while (col < _frame.Cols)
            {
                var visual = VisualAt(col, row, defaultFg, defaultBg, selection, cursorSpan);
                var span = 1;
                while (col + span < _frame.Cols
                       && VisualAt(col + span, row, defaultFg, defaultBg, selection, cursorSpan).Background
                           == visual.Background)
                {
                    span++;
                }
                if (visual.Background != defaultBg)
                {
                    ds.FillRectangle(
                        new Rect(col * _cellWidth, y, span * _cellWidth, _cellHeight),
                        visual.Background);
                }
                col += span;
            }

            for (col = 0; col < _frame.Cols; col++)
            {
                var index = row * _frame.Cols + col;
                if (index >= _cellCount)
                {
                    break;
                }
                var cell = _cells[index];
                if (cell.Width == 0)
                {
                    continue;
                }
                var visual = VisualAt(col, row, defaultFg, defaultBg, selection, cursorSpan);
                if (!visual.GlyphVisible)
                {
                    continue;
                }
                if (cell.Ch != 0)
                {
                    var text = CmuxNative.TextOf(cell);
                    if (text.Length == 0)
                    {
                        text = char.ConvertFromUtf32((int)cell.Ch);
                    }
                    var layout = GlyphLayout(sender, text, StyleIndex(cell.Attrs), cell.Width);
                    ds.DrawTextLayout(layout, col * _cellWidth, y, visual.Foreground);
                }
                DrawDecorations(ds, cell, col * _cellWidth, y, visual.Foreground);
            }
        }

        DrawNonBlockCursor(ds, defaultFg, cursorSpan);
    }

    private readonly record struct GlyphLayoutKey(string Text, int Style, byte Width);

    private readonly record struct CellVisual(Color Foreground, Color Background, bool GlyphVisible);

    private CanvasTextLayout GlyphLayout(CanvasControl sender, string text, int style, byte width)
    {
        var key = new GlyphLayoutKey(text, style, width);
        if (_glyphLayouts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var layout = new CanvasTextLayout(
            sender,
            text,
            _formats[style]!,
            _cellWidth * Math.Max(1, (int)width),
            _cellHeight);
        _glyphLayouts.Add(key, layout);
        _glyphLayoutOrder.Enqueue(key);
        while (_glyphLayouts.Count > GlyphLayoutCapacity && _glyphLayoutOrder.TryDequeue(out var oldest))
        {
            if (_glyphLayouts.Remove(oldest, out var evicted))
            {
                evicted.Dispose();
            }
        }
        return layout;
    }

    private CellVisual VisualAt(
        int col,
        int row,
        Color defaultFg,
        Color defaultBg,
        ((int Col, int Row) Start, (int Col, int Row) End)? selection,
        (int Col, int Row, int Width)? cursorSpan)
    {
        var index = row * _frame.Cols + col;
        if (index < 0 || index >= _cellCount)
        {
            return new CellVisual(defaultFg, defaultBg, false);
        }

        var cell = _cells[index];
        var foreground = FromPacked(cell.Fg, defaultFg);
        var background = FromPacked(cell.Bg, defaultBg);
        if (Has(cell.Attrs, AttrInverse))
        {
            (foreground, background) = (background, foreground);
        }

        var cellFirstCol = col;
        var cellLastCol = col + Math.Max(1, (int)cell.Width) - 1;
        if (cell.Width == 0 && col > 0)
        {
            var leadIndex = index - 1;
            if (leadIndex >= 0 && _cells[leadIndex].Width == 2)
            {
                cellFirstCol--;
            }
        }
        var selected = selection is { } range
            && (row > range.Start.Row || (row == range.Start.Row && cellLastCol >= range.Start.Col))
            && (row < range.End.Row || (row == range.End.Row && cellFirstCol <= range.End.Col));
        if (selected)
        {
            background = FromPacked(_selectionBackground, defaultFg);
            foreground = FromPacked(_selectionForeground, defaultBg);
        }
        if (Has(cell.Attrs, AttrFaint))
        {
            foreground = Blend(foreground, background, 0.55f);
        }

        var blockCursor = cursorSpan is { } cursor
            && _frame.CursorShape == 1
            && (_frame.CursorBlink == 0 || _blinkVisible)
            && row == cursor.Row
            && col >= cursor.Col
            && col < cursor.Col + cursor.Width;
        if (blockCursor)
        {
            background = FromPacked(_frame.CursorColor, defaultFg);
            foreground = defaultBg;
        }

        var visible = !Has(cell.Attrs, AttrInvisible)
            && (!Has(cell.Attrs, AttrBlink) || _blinkVisible);
        return new CellVisual(foreground, background, visible);
    }

    private (int Col, int Row, int Width)? CursorSpan()
    {
        if (_frame.CursorVisible == 0
            || _frame.CursorRow >= _frame.Rows
            || _frame.CursorCol >= _frame.Cols)
        {
            return null;
        }

        var col = (int)_frame.CursorCol;
        var row = (int)_frame.CursorRow;
        var index = row * _frame.Cols + col;
        if (index < _cellCount && _cells[index].Width == 0 && col > 0)
        {
            col--;
            index--;
        }
        var width = index < _cellCount ? Math.Max(1, (int)_cells[index].Width) : 1;
        return (col, row, width);
    }

    private void DrawNonBlockCursor(
        CanvasDrawingSession drawing,
        Color defaultForeground,
        (int Col, int Row, int Width)? span)
    {
        if (span is not { } cursor
            || _frame.CursorShape == 1
            || (_frame.CursorBlink != 0 && !_blinkVisible))
        {
            return;
        }

        var color = FromPacked(_frame.CursorColor, defaultForeground);
        var x = cursor.Col * _cellWidth;
        var y = cursor.Row * _cellHeight;
        var width = cursor.Width * _cellWidth;
        switch (_frame.CursorShape)
        {
            case 2:
                drawing.FillRectangle(new Rect(x, y + _cellHeight - 2, width, 2), color);
                break;
            case 3:
                drawing.FillRectangle(new Rect(x, y, 2, _cellHeight), color);
                break;
            default:
                drawing.DrawRectangle(new Rect(x, y, width, _cellHeight), color, 1);
                break;
        }
    }

    // Never log key or character content here. A terminal carries passwords,
    // SSH passphrases, and pasted secrets, so logging input would write them
    // to disk in plaintext. Diagnose focus and routing instead, which is what
    // actually goes wrong.

    private void DrawDecorations(
        CanvasDrawingSession drawing,
        in CmuxNative.Cell cell,
        float x,
        float y,
        Color colour)
    {
        if (cell.Underline == 0 && !Has(cell.Attrs, AttrStrikethrough))
        {
            return;
        }
        var width = _cellWidth * Math.Max(1, (int)cell.Width);
        if (Has(cell.Attrs, AttrStrikethrough))
        {
            var strikeY = y + _cellHeight * 0.55f;
            drawing.DrawLine(x, strikeY, x + width, strikeY, colour, 1);
        }
        if (cell.Underline == 0)
        {
            return;
        }
        var underlineY = y + _cellHeight - 2;
        switch (cell.Underline)
        {
            case 2:
                drawing.DrawLine(x, underlineY - 2, x + width, underlineY - 2, colour, 1);
                drawing.DrawLine(x, underlineY, x + width, underlineY, colour, 1);
                break;
            case 3:
                for (var offset = 0f; offset < width; offset += 4)
                {
                    drawing.DrawLine(
                        x + offset,
                        underlineY + ((int)(offset / 4) % 2 == 0 ? -1 : 1),
                        x + Math.Min(width, offset + 4),
                        underlineY + ((int)(offset / 4) % 2 == 0 ? 1 : -1),
                        colour,
                        1);
                }
                break;
            case 4:
                for (var offset = 0f; offset < width; offset += 3)
                {
                    drawing.FillCircle(x + offset, underlineY, 0.7f, colour);
                }
                break;
            case 5:
                for (var offset = 0f; offset < width; offset += 6)
                {
                    drawing.DrawLine(x + offset, underlineY, x + Math.Min(width, offset + 4), underlineY, colour, 1);
                }
                break;
            default:
                drawing.DrawLine(x, underlineY, x + width, underlineY, colour, 1);
                break;
        }
    }

    private const ushort AttrBold = 0x0001;
    private const ushort AttrItalic = 0x0002;
    private const ushort AttrStrikethrough = 0x0004;
    private const ushort AttrInverse = 0x0008;
    private const ushort AttrFaint = 0x0010;
    private const ushort AttrInvisible = 0x0020;
    private const ushort AttrBlink = 0x0040;

    private static bool Has(ushort attrs, ushort bit) => (attrs & bit) != 0;

    /// <summary>Only the bits that change how a run is painted.</summary>
    private static ushort StyleOf(ushort attrs) =>
        (ushort)(attrs & (AttrBold | AttrItalic | AttrInverse | AttrFaint));

    private static int StyleIndex(ushort style) =>
        (Has(style, AttrBold) ? 1 : 0) | (Has(style, AttrItalic) ? 2 : 0);

    private static Color Blend(Color foreground, Color background, float amount) =>
        Color.FromArgb(
            foreground.A,
            (byte)Math.Round(background.R + (foreground.R - background.R) * amount),
            (byte)Math.Round(background.G + (foreground.G - background.G) * amount),
            (byte)Math.Round(background.B + (foreground.B - background.B) * amount));

    internal void ForwardKeyDown(KeyRoutedEventArgs args) => OnKeyDown(this, args);

    internal bool ForwardKeyDown(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status) =>
        HandleKeyDown(key, status);

    internal void ForwardKeyUp(KeyRoutedEventArgs args) => OnKeyUp(this, args);

    internal bool ForwardKeyUp(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status) =>
        HandleKeyUp(key, status);

    internal void ForwardCharacterReceived(CharacterReceivedRoutedEventArgs args) =>
        OnCharacterReceived(this, args);

    internal bool HandleShortcut(ShortcutAction action)
    {
        if (!_hostActive)
        {
            return false;
        }
        switch (action)
        {
            case ShortcutAction.TerminalCopy:
                CopySelection();
                return true;
            case ShortcutAction.TerminalPaste:
                _ = PasteAsync();
                return true;
            case ShortcutAction.TerminalSelectAll:
                SelectAll();
                return true;
            default:
                return false;
        }
    }

    internal bool ForwardCharacterReceived(uint keyCode)
    {
        var text = keyCode <= char.MaxValue
            ? ((char)keyCode).ToString()
            : keyCode <= 0x10FFFF
                ? char.ConvertFromUtf32((int)keyCode)
                : string.Empty;
        return text.Length > 0 && HandleCharacterReceived(text);
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (HandleCharacterReceived(args.Character.ToString()))
        {
            args.Handled = true;
        }
    }

    private bool HandleCharacterReceived(string text)
    {
        if (!_hostActive)
        {
            return false;
        }
        if (_suppressStructuredCharacter)
        {
            _suppressStructuredCharacter = false;
            return true;
        }
        // Printable input, including anything produced by an IME.
        Send(Encoding.UTF8.GetBytes(text));
        return true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleKeyDown(e.Key, e.KeyStatus))
        {
            e.Handled = true;
        }
    }

    private bool HandleKeyDown(
        VirtualKey virtualKey,
        Windows.UI.Core.CorePhysicalKeyStatus status)
    {
        if (!_hostActive)
        {
            return false;
        }
        var key = KeyName(virtualKey, status);
        var control = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var shift = IsKeyDown(VirtualKey.Shift);
        var super = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);
        var rightShift = IsKeyDown(VirtualKey.RightShift);
        var rightControl = IsKeyDown(VirtualKey.RightControl);
        var rightAlt = IsKeyDown(VirtualKey.RightMenu);
        var rightSuper = IsKeyDown(VirtualKey.RightWindows);
        var capsLock = IsLocked(VirtualKey.CapitalLock);
        var numLock = IsLocked(VirtualKey.NumberKeyLock);
        if (rightAlt && control)
        {
            control = false;
            alt = false;
            rightControl = false;
            rightAlt = false;
            if (virtualKey is VirtualKey.Control or VirtualKey.LeftControl
                or VirtualKey.RightControl or VirtualKey.Menu
                or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            {
                ReleaseStructuredControlKeys();
                return false;
            }
        }
        if (key is null || (!control && !alt && !super && IsPrintableKey(virtualKey)))
        {
            return false;
        }

        var chord = new StringBuilder();
        if (rightControl)
        {
            chord.Append("rctrl+");
        }
        else if (control)
        {
            chord.Append("ctrl+");
        }
        if (rightAlt)
        {
            chord.Append("ralt+");
        }
        else if (alt)
        {
            chord.Append("alt+");
        }
        if (rightShift)
        {
            chord.Append("rshift+");
        }
        else if (shift)
        {
            chord.Append("shift+");
        }
        if (rightSuper)
        {
            chord.Append("rsuper+");
        }
        else if (super)
        {
            chord.Append("super+");
        }
        if (capsLock)
        {
            chord.Append("caps+");
        }
        if (numLock)
        {
            chord.Append("num+");
        }
        chord.Append(key);
        var value = chord.ToString();
        var identity = (virtualKey, status.ScanCode, status.IsExtendedKey);
        var action = status.WasKeyDown ? (byte)2 : (byte)1;
        _structuredKeysDown[identity] = value;
        if (NumpadProducesCharacter(key))
        {
            _suppressStructuredCharacter = true;
            DispatcherQueue.TryEnqueue(() => _suppressStructuredCharacter = false);
        }
        SendKey(value, action);
        return true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (HandleKeyUp(e.Key, e.KeyStatus))
        {
            e.Handled = true;
        }
    }

    private bool HandleKeyUp(
        VirtualKey virtualKey,
        Windows.UI.Core.CorePhysicalKeyStatus status)
    {
        if (!_hostActive)
        {
            return false;
        }
        var identity = (virtualKey, status.ScanCode, status.IsExtendedKey);
        if (!_structuredKeysDown.Remove(identity, out var chord))
        {
            return false;
        }
        SendKey(chord, 0);
        return true;
    }

    private static bool IsPrintableKey(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z
        || key is >= VirtualKey.Number0 and <= VirtualKey.Number9
        || key is VirtualKey.Space
        || (int)key is >= 0xBA and <= 0xC0
        || (int)key is >= 0xDB and <= 0xDE
        || (int)key == 0xE2;

    private static bool NumpadProducesCharacter(string key) => key is
        "numpad0" or "numpad1" or "numpad2" or "numpad3" or "numpad4"
        or "numpad5" or "numpad6" or "numpad7" or "numpad8" or "numpad9"
        or "numpadadd" or "numpadcomma" or "numpaddecimal" or "numpaddivide"
        or "numpadequal" or "numpadmultiply" or "numpadseparator" or "numpadsubtract";

    private static string? KeyName(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status)
    {
        if (NumpadKeyName(key, status) is { } numpad)
        {
            return numpad;
        }
        if (ModifierKeyName(key, status) is { } modifier)
        {
            return modifier;
        }
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return ((char)('a' + (int)key - (int)VirtualKey.A)).ToString();
        }
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((char)('0' + (int)key - (int)VirtualKey.Number0)).ToString();
        }
        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            return $"f{(int)key - (int)VirtualKey.F1 + 1}";
        }
        return key switch
        {
            VirtualKey.Enter => "enter",
            VirtualKey.Back => "backspace",
            VirtualKey.Tab => "tab",
            VirtualKey.Escape => "escape",
            VirtualKey.Insert => "insert",
            VirtualKey.Delete => "delete",
            VirtualKey.Up => "up",
            VirtualKey.Down => "down",
            VirtualKey.Right => "right",
            VirtualKey.Left => "left",
            VirtualKey.Home => "home",
            VirtualKey.End => "end",
            VirtualKey.PageUp => "pageup",
            VirtualKey.PageDown => "pagedown",
            VirtualKey.Space => "space",
            VirtualKey.NumberKeyLock => "numlock",
            VirtualKey.CapitalLock => "capslock",
            VirtualKey.Scroll => "scrolllock",
            VirtualKey.Snapshot => "printscreen",
            VirtualKey.Pause => "pause",
            VirtualKey.Application => "contextmenu",
            VirtualKey.Convert => "convert",
            VirtualKey.NonConvert => "nonconvert",
            VirtualKey.Kana => "kanamode",
            _ => OemKeyName(key),
        };
    }

    private static string? ModifierKeyName(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status) => key switch
    {
        VirtualKey.Shift => status.ScanCode == 0x36 ? "shiftright" : "shiftleft",
        VirtualKey.LeftShift => "shiftleft",
        VirtualKey.RightShift => "shiftright",
        VirtualKey.Control => status.IsExtendedKey ? "controlright" : "controlleft",
        VirtualKey.LeftControl => "controlleft",
        VirtualKey.RightControl => "controlright",
        VirtualKey.Menu => status.IsExtendedKey ? "altright" : "altleft",
        VirtualKey.LeftMenu => "altleft",
        VirtualKey.RightMenu => "altright",
        VirtualKey.LeftWindows => "metaleft",
        VirtualKey.RightWindows => "metaright",
        _ => null,
    };

    private static string? OemKeyName(VirtualKey key) => (int)key switch
    {
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => null,
    };

    private static string? NumpadKeyName(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status)
    {
        if ((int)key is >= 0x60 and <= 0x69)
        {
            return $"numpad{(int)key - 0x60}";
        }
        var direct = (int)key switch
        {
            0x6A => "numpadmultiply",
            0x6B => "numpadadd",
            0x6C => "numpadseparator",
            0x6D => "numpadsubtract",
            0x6E => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ","
                ? "numpadcomma"
                : "numpaddecimal",
            0x6F => "numpaddivide",
            0x92 => "numpadequal",
            _ => null,
        };
        if (direct is not null)
        {
            return direct;
        }
        if (status.IsExtendedKey)
        {
            return status.ScanCode switch
            {
                0x1C => "numpadenter",
                0x35 => "numpaddivide",
                _ => null,
            };
        }
        return status.ScanCode switch
        {
            0x47 => key == VirtualKey.Home ? "numpadhome" : "numpad7",
            0x48 => key == VirtualKey.Up ? "numpadup" : "numpad8",
            0x49 => key == VirtualKey.PageUp ? "numpadpageup" : "numpad9",
            0x4B => key == VirtualKey.Left ? "numpadleft" : "numpad4",
            0x4C => key == VirtualKey.Clear ? "numpadbegin" : "numpad5",
            0x4D => key == VirtualKey.Right ? "numpadright" : "numpad6",
            0x4F => key == VirtualKey.End ? "numpadend" : "numpad1",
            0x50 => key == VirtualKey.Down ? "numpaddown" : "numpad2",
            0x51 => key == VirtualKey.PageDown ? "numpadpagedown" : "numpad3",
            0x52 => key == VirtualKey.Insert ? "numpadinsert" : "numpad0",
            0x53 => key == VirtualKey.Delete ? "numpaddelete" : "numpaddecimal",
            0x37 => "numpadmultiply",
            0x4A => "numpadsubtract",
            0x4E => "numpadadd",
            _ => null,
        };
    }

    private void ReleaseStructuredControlKeys()
    {
        foreach (var identity in _structuredKeysDown.Keys
                     .Where(identity => identity.Key is VirtualKey.Control
                         or VirtualKey.LeftControl or VirtualKey.RightControl
                         or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
                     .ToList())
        {
            if (_structuredKeysDown.Remove(identity, out var chord))
            {
                SendKey(chord, 0);
            }
        }
    }

    private void ResetStructuredKeyState()
    {
        foreach (var chord in _structuredKeysDown.Values)
        {
            SendKey(chord, 0);
        }
        _structuredKeysDown.Clear();
        _suppressStructuredCharacter = false;
    }

    private void SendKey(string chord, byte action)
    {
        if (_session == IntPtr.Zero)
        {
            _pendingKeys.Add((chord, action));
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(chord);
        if (CmuxNative.SessionKeyEvent(_session, bytes, (nuint)bytes.Length, action) != 0)
        {
            Diag.Log("terminal key event failed");
        }
    }

    private void Send(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }
        if (_session == IntPtr.Zero)
        {
            // The window is visible for a moment before the session exists, and
            // anything typed in that gap used to vanish. Hold it instead, and
            // let the shell receive it once there is somewhere to put it.
            _pending.AddRange(bytes);
            return;
        }
        CmuxNative.SessionWrite(_session, bytes, (nuint)bytes.Length);
    }

    /// <summary>Deliver anything typed before the session existed.</summary>
    private void FlushPendingInput()
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        if (_pending.Count > 0)
        {
            var bytes = _pending.ToArray();
            _pending.Clear();
            CmuxNative.SessionWrite(_session, bytes, (nuint)bytes.Length);
        }
        if (_pendingKeys.Count > 0)
        {
            var keys = _pendingKeys.ToArray();
            _pendingKeys.Clear();
            foreach (var (chord, action) in keys)
            {
                SendKey(chord, action);
            }
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static bool IsLocked(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Locked) != 0;

    private static Color FromPacked(uint packed, Color fallback)
    {
        if (packed == CmuxNative.NoColor)
        {
            return fallback;
        }
        return Color.FromArgb(
            255,
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    private void DisposeTextResources()
    {
        foreach (var layout in _glyphLayouts.Values)
        {
            layout.Dispose();
        }
        _glyphLayouts.Clear();
        _glyphLayoutOrder.Clear();
        foreach (var format in _formats)
        {
            format?.Dispose();
        }
        Array.Clear(_formats);
        _format?.Dispose();
        _format = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        ResetStructuredKeyState();
        _disposed = true;

        AppSettings.Changed -= OnSettingsChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        LayoutUpdated -= OnPostArrangeLayoutUpdated;
        _postArrangeRefreshPending = false;
        _canvas.SizeChanged -= OnCanvasSizeChanged;
        _timer.Stop();
        DisposeTextResources();
        if (_session != IntPtr.Zero)
        {
            CmuxNative.SessionFree(_session);
            _session = IntPtr.Zero;
        }
        if (_canvasAttached)
        {
            _canvas.RemoveFromVisualTree();
            _canvasAttached = false;
        }
    }
}
