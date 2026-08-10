using System;
using System.Collections.Generic;
using System.Text;
using CmuxGui.Interop;
using CmuxGui.Services;
using Microsoft.Graphics.Canvas;
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

    private CanvasTextFormat _format = null!;
    // Bold and italic need their own formats; a single one renders every
    // attribute as regular text, which flattens any TUI that uses emphasis.
    private readonly CanvasTextFormat[] _formats = new CanvasTextFormat[4];
    private string _status = string.Empty;
    // Used until the first snapshot arrives, so the very first paint already
    // shows the Ghostty background instead of flashing a default.
    private uint _themeBackground = CmuxNative.NoColor;
    private uint _themeForeground = CmuxNative.NoColor;
    private uint _selectionBackground = CmuxNative.NoColor;
    private float _cellWidth = 8;
    private float _cellHeight = 16;
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
    private bool _disposed;

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
        _timer.Tick += (_, _) => Poll();

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
            foreach (var chord in _structuredKeysDown.Values)
            {
                SendKey(chord, 0);
            }
            _structuredKeysDown.Clear();
            var holder = XamlRoot is null
                ? "no-xaml-root"
                : FocusManager.GetFocusedElement(XamlRoot)?.GetType().FullName ?? "nothing";
            Diag.Log($"terminal lost focus; now held by {holder}");
        };
    }

    public string TabId => _tabId;

    private void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // Adopt the user's Ghostty font and colours. The engine already resolves
        // cell colours through the theme palette; what is needed here is the
        // face to shape with and the surface colour behind the grid.
        CmuxNative.ThemeLoad(out var theme);
        var family = CmuxNative.FontFamilyOf(theme);
        Diag.Log($"CreateResources theme loaded={theme.Loaded} font='{family}' size={theme.FontSize}");

        _themeBackground = theme.Background;
        _themeForeground = theme.Foreground;
        _selectionBackground = theme.SelectionBackground;

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
                FontWeight = (i & 1) != 0
                    ? Microsoft.UI.Text.FontWeights.Bold
                    : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = (i & 2) != 0
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal,
            };
        }

        // Measure rather than assume: the resolved face decides the cell box.
        using var layout = new CanvasTextLayout(sender, "MMMMMMMMMM", _format, 0, 0);
        _cellWidth = (float)layout.LayoutBounds.Width / 10f;
        _cellHeight = (float)layout.LayoutBounds.Height;
        _metricsReady = true;
        SyncGrid();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diag.Log($"Loaded size={ActualWidth}x{ActualHeight}");
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
        _timer.Start();
        // Loaded can run before the window is activated, and focus does not
        // stick then. Queue a second attempt once the tree is live.
        TakeFocus("loaded", FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() => TakeFocus("queued", FocusState.Programmatic));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) =>
        RequestPostArrangeRefresh();

    internal void NotifyHostReparented() => RequestPostArrangeRefresh();

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
        if (_session == IntPtr.Zero)
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
            _canvas.Invalidate();
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        // Before the first snapshot the frame carries no colours yet.
        var background = _frame.Cols == 0 ? _themeBackground : _frame.DefaultBg;
        var opaque = FromPacked(background, Colors.Black);
        var opacity = AppSettings.Current.TerminalOpacity;
        // Blending every frame against the backdrop is what makes the window
        // shimmer, so only do it when transparency was actually asked for.
        ds.Clear(opacity >= 0.999
            ? opaque
            : Color.FromArgb((byte)Math.Clamp(opacity * 255.0, 0, 255), opaque.R, opaque.G, opaque.B));

        if (_cellCount == 0 || _frame.Cols == 0)
        {
            var why = string.IsNullOrEmpty(_status)
                ? $"waiting for first frame (session={(_session == IntPtr.Zero ? "null" : "ok")}, "
                  + $"grid={_cols}x{_rows}, cell={_cellWidth:F1}x{_cellHeight:F1}, "
                  + $"size={ActualWidth:F0}x{ActualHeight:F0})"
                : _status;
            ds.DrawText(why, 12, 12, Colors.OrangeRed, _format);
            return;
        }

        var defaultFg = FromPacked(_frame.DefaultFg, FromPacked(_themeForeground, Colors.White));

        for (var row = 0; row < _frame.Rows; row++)
        {
            var y = row * _cellHeight;

            // Backgrounds first, coalescing equal runs into one rectangle.
            var col = 0;
            while (col < _frame.Cols)
            {
                var index = row * _frame.Cols + col;
                if (index >= _cellCount)
                {
                    break;
                }
                var bg = EffectiveBg(_cells[index]);
                if (bg == CmuxNative.NoColor)
                {
                    col++;
                    continue;
                }
                var span = 1;
                while (col + span < _frame.Cols
                       && row * _frame.Cols + col + span < _cellCount
                       && EffectiveBg(_cells[row * _frame.Cols + col + span]) == bg)
                {
                    span++;
                }
                ds.FillRectangle(
                    new Rect(col * _cellWidth, y, span * _cellWidth, _cellHeight),
                    FromPacked(bg, Colors.Transparent));
                col += span;
            }

            // Then glyphs, one draw per contiguous same-colour run.
            col = 0;
            var run = new StringBuilder();
            while (col < _frame.Cols)
            {
                var index = row * _frame.Cols + col;
                if (index >= _cellCount)
                {
                    break;
                }
                var cell = _cells[index];
                if (cell.Ch == 0 || cell.Width == 0 || Has(cell.Attrs, AttrInvisible))
                {
                    col++;
                    continue;
                }

                var fg = cell.Fg;
                var style = StyleOf(cell.Attrs);
                var start = col;
                run.Clear();
                while (col < _frame.Cols)
                {
                    var i = row * _frame.Cols + col;
                    if (i >= _cellCount)
                    {
                        break;
                    }
                    var c = _cells[i];
                    if (c.Width == 0)
                    {
                        col++;
                        continue;
                    }
                    // A run must share colour *and* style, or bold and dim text
                    // would inherit whatever the run happened to start with.
                    if (c.Ch == 0 || c.Fg != fg || StyleOf(c.Attrs) != style
                        || Has(c.Attrs, AttrInvisible))
                    {
                        break;
                    }
                    var text = CmuxNative.TextOf(c);
                    run.Append(text.Length == 0 ? char.ConvertFromUtf32((int)c.Ch) : text);
                    col++;
                }

                if (run.Length > 0)
                {
                    var colour = Has(style, AttrInverse)
                        ? FromPacked(_frame.DefaultBg, Colors.Black)
                        : (fg == CmuxNative.NoColor ? defaultFg : FromPacked(fg, defaultFg));
                    if (Has(style, AttrFaint))
                    {
                        colour = Dim(colour);
                    }
                    ds.DrawText(run.ToString(), start * _cellWidth, y, colour,
                                _formats[StyleIndex(style)]);
                }
            }

            for (col = 0; col < _frame.Cols; col++)
            {
                var index = row * _frame.Cols + col;
                if (index >= _cellCount || _cells[index].Width == 0)
                {
                    continue;
                }
                DrawDecorations(ds, _cells[index], col * _cellWidth, y, defaultFg);
            }
        }

        // Selection sits above the cells and below the cursor, tinted rather than
        // opaque so the text under it stays readable.
        if (SelectionRange() is { } sel)
        {
            var selection = FromPacked(_selectionBackground, Color.FromArgb(255, 0x0A, 0x84, 0xFF));
            var tint = Color.FromArgb(110, selection.R, selection.G, selection.B);
            for (var row = sel.Start.Row; row <= sel.End.Row && row < _frame.Rows; row++)
            {
                var first = row == sel.Start.Row ? sel.Start.Col : 0;
                var last = row == sel.End.Row ? sel.End.Col : _frame.Cols - 1;
                ds.FillRectangle(
                    new Rect(first * _cellWidth, row * _cellHeight,
                             Math.Max(1, last - first + 1) * _cellWidth, _cellHeight),
                    tint);
            }
        }

        if (_frame.CursorVisible != 0)
        {
            var cursor = FromPacked(_frame.CursorColor, defaultFg);
            var x = _frame.CursorCol * _cellWidth;
            var y = _frame.CursorRow * _cellHeight;
            switch (_frame.CursorShape)
            {
                case 2:
                    ds.FillRectangle(new Rect(x, y + _cellHeight - 2, _cellWidth, 2), cursor);
                    break;
                case 3:
                    ds.FillRectangle(new Rect(x, y, 2, _cellHeight), cursor);
                    break;
                case 4:
                    ds.DrawRectangle(new Rect(x, y, _cellWidth, _cellHeight), cursor, 1);
                    break;
                default:
                    ds.FillRectangle(new Rect(x, y, _cellWidth, _cellHeight), cursor);
                    break;
            }
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
        Color defaultForeground)
    {
        if (cell.Underline == 0 && !Has(cell.Attrs, AttrStrikethrough))
        {
            return;
        }
        var colour = cell.Fg == CmuxNative.NoColor
            ? defaultForeground
            : FromPacked(cell.Fg, defaultForeground);
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

    private static bool Has(ushort attrs, ushort bit) => (attrs & bit) != 0;

    /// <summary>Only the bits that change how a run is painted.</summary>
    private static ushort StyleOf(ushort attrs) =>
        (ushort)(attrs & (AttrBold | AttrItalic | AttrInverse | AttrFaint));

    private static int StyleIndex(ushort style) =>
        (Has(style, AttrBold) ? 1 : 0) | (Has(style, AttrItalic) ? 2 : 0);

    /// <summary>Faint text is the same colour carried toward the background.</summary>
    private static Color Dim(Color c) =>
        Color.FromArgb(c.A, (byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55));

    /// <summary>Cell background, with inverse swapping the foreground in.</summary>
    private uint EffectiveBg(in CmuxNative.Cell cell)
    {
        if (Has(cell.Attrs, AttrInverse))
        {
            return cell.Fg == CmuxNative.NoColor ? _frame.DefaultFg : cell.Fg;
        }
        return cell.Bg;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_suppressStructuredCharacter)
        {
            _suppressStructuredCharacter = false;
            args.Handled = true;
            return;
        }
        // Printable input, including anything produced by an IME.
        Send(Encoding.UTF8.GetBytes(args.Character.ToString()));
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = KeyName(e);
        var control = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var shift = IsKeyDown(VirtualKey.Shift);
        if (key is null || (!control && !alt && IsPrintableKey(e.Key)))
        {
            return;
        }

        var chord = new StringBuilder();
        if (control)
        {
            chord.Append("ctrl+");
        }
        if (alt)
        {
            chord.Append("alt+");
        }
        if (shift)
        {
            chord.Append("shift+");
        }
        chord.Append(key);
        var value = chord.ToString();
        var identity = (e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        var action = e.KeyStatus.WasKeyDown ? (byte)2 : (byte)1;
        _structuredKeysDown[identity] = value;
        if (NumpadProducesCharacter(key))
        {
            _suppressStructuredCharacter = true;
            DispatcherQueue.TryEnqueue(() => _suppressStructuredCharacter = false);
        }
        SendKey(value, action);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var identity = (e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        if (_structuredKeysDown.Remove(identity, out var chord))
        {
            SendKey(chord, 0);
            e.Handled = true;
        }
    }

    private static bool IsPrintableKey(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z
        || key is >= VirtualKey.Number0 and <= VirtualKey.Number9
        || key is VirtualKey.Space;

    private static bool NumpadProducesCharacter(string key) => key is
        "numpad0" or "numpad1" or "numpad2" or "numpad3" or "numpad4"
        or "numpad5" or "numpad6" or "numpad7" or "numpad8" or "numpad9"
        or "numpadadd" or "numpadcomma" or "numpaddecimal" or "numpaddivide"
        or "numpadequal" or "numpadmultiply" or "numpadseparator" or "numpadsubtract";

    private static string? KeyName(KeyRoutedEventArgs args)
    {
        var key = args.Key;
        if (NumpadKeyName(key, args.KeyStatus) is { } numpad)
        {
            return numpad;
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
            _ => null,
        };
    }

    private static string? NumpadKeyName(
        VirtualKey key,
        Windows.UI.Core.CorePhysicalKeyStatus status)
    {
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

    private void SendKey(string chord, byte action)
    {
        if (_session == IntPtr.Zero)
        {
            _pendingKeys.Add((chord, action));
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(chord);
        CmuxNative.SessionKeyEvent(_session, bytes, (nuint)bytes.Length, action);
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

    private static bool IsKeyDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        AppSettings.Changed -= OnSettingsChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        LayoutUpdated -= OnPostArrangeLayoutUpdated;
        _postArrangeRefreshPending = false;
        _canvas.SizeChanged -= OnCanvasSizeChanged;
        _timer.Stop();
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
