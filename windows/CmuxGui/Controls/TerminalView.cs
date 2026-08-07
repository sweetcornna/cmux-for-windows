using System;
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

    private IntPtr _session;
    private CmuxNative.Cell[] _cells = Array.Empty<CmuxNative.Cell>();
    private CmuxNative.Frame _frame;
    private int _cellCount;

    private CanvasTextFormat _format = null!;
    private string _status = string.Empty;
    // Used until the first snapshot arrives, so the very first paint already
    // shows the Ghostty background instead of flashing a default.
    private uint _themeBackground = CmuxNative.NoColor;
    private uint _themeForeground = CmuxNative.NoColor;
    private float _cellWidth = 8;
    private float _cellHeight = 16;
    private ushort _cols;
    private ushort _rows;

    public TerminalView()
    {
        _root.Children.Add(_backgroundImage);
        _root.Children.Add(_canvas);
        Content = _root;
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        // Transparent clear lets the terminal background carry an alpha and
        // composite over the image and the window backdrop beneath it.
        _canvas.ClearColor = Colors.Transparent;
        AppSettings.Changed += OnSettingsChanged;

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += OnCreateResources;
        _canvas.SizeChanged += (_, _) => SyncGrid();

        // The engine has no GUI wakeup yet, so poll near display rate. Redraws
        // are skipped unless the snapshot reports damage.
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Poll();

        Diag.Log("TerminalView ctor");
        Loaded += OnLoaded;
        // Deliberately NOT disposing on Unloaded: TabView unloads and reloads
        // its content during setup and on every tab switch, so tearing the
        // session down there kills the terminal before it ever draws. The
        // owning tab disposes this explicitly when it is closed.
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
    }

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

        _format = new CanvasTextFormat
        {
            FontFamily = string.IsNullOrWhiteSpace(family) ? "Cascadia Mono" : family,
            FontSize = theme.FontSize > 0 ? theme.FontSize : 14,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        // Measure rather than assume: the resolved face decides the cell box.
        using var layout = new CanvasTextLayout(sender, "MMMMMMMMMM", _format, 0, 0);
        _cellWidth = (float)layout.LayoutBounds.Width / 10f;
        _cellHeight = (float)layout.LayoutBounds.Height;
        SyncGrid();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diag.Log($"Loaded size={ActualWidth}x{ActualHeight}");
        SyncGrid();
        _timer.Start();
        Focus(FocusState.Programmatic);
    }



    /// <summary>Match the PTY grid to the control size, creating the session on first use.</summary>
    private void SyncGrid()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            Diag.Log($"SyncGrid skipped cell={_cellWidth}x{_cellHeight} size={ActualWidth}x{ActualHeight}");
            return;
        }

        var cols = (ushort)Math.Max(1, (int)(ActualWidth / _cellWidth));
        var rows = (ushort)Math.Max(1, (int)(ActualHeight / _cellHeight));
        if (cols == _cols && rows == _rows && _session != IntPtr.Zero)
        {
            return;
        }

        _cols = cols;
        _rows = rows;

        if (_session == IntPtr.Zero)
        {
            try
            {
                _session = CmuxNative.SessionNew(cols, rows);
                Diag.Log($"SessionNew({cols},{rows}) -> {_session}");
                _status = _session == IntPtr.Zero
                    ? "cmux_session_new returned null"
                    : string.Empty;
                if (_session != IntPtr.Zero)
                {
                    ApplySettings();
                }
            }
            catch (Exception ex)
            {
                // A failure here is otherwise invisible: the canvas just stays
                // blank and the app looks broken with no explanation.
                _status = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        else
        {
            CmuxNative.SessionResize(_session, cols, rows);
        }

        var needed = cols * rows;
        if (_cells.Length < needed)
        {
            _cells = new CmuxNative.Cell[needed];
        }
    }

    private void OnSettingsChanged() => ApplySettings();

    /// <summary>Push the selected theme into the engine and refresh the surface.</summary>
    private void ApplySettings()
    {
        var settings = AppSettings.Current;

        if (_session != IntPtr.Zero && !string.IsNullOrWhiteSpace(settings.Theme))
        {
            var text = ThemeCatalog.Read(settings.Theme);
            if (text is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                CmuxNative.SessionApplyThemeText(_session, bytes, (nuint)bytes.Length);
            }
            else
            {
                Diag.Log($"theme '{settings.Theme}' not found");
            }
        }

        _backgroundImage.Opacity = settings.BackgroundImageOpacity;
        _backgroundImage.Source = null;
        if (!string.IsNullOrWhiteSpace(settings.BackgroundImagePath)
            && System.IO.File.Exists(settings.BackgroundImagePath))
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

    private void Poll()
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
        // Cursor blink and output both surface as damage, so this is the only
        // condition that needs a repaint.
        if (frame.Dirty != 0)
        {
            _canvas.Invalidate();
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Diag.Log($"OnDraw cells={_cellCount} grid={_frame.Cols}x{_frame.Rows} bg=0x{_frame.DefaultBg:X6} fg=0x{_frame.DefaultFg:X6}");
        var ds = args.DrawingSession;
        // Before the first snapshot the frame carries no colours yet.
        var background = _frame.Cols == 0 ? _themeBackground : _frame.DefaultBg;
        var opaque = FromPacked(background, Colors.Black);
        var alpha = (byte)Math.Clamp(AppSettings.Current.TerminalOpacity * 255.0, 0, 255);
        ds.Clear(Color.FromArgb(alpha, opaque.R, opaque.G, opaque.B));

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
                var bg = _cells[index].Bg;
                if (bg == CmuxNative.NoColor)
                {
                    col++;
                    continue;
                }
                var span = 1;
                while (col + span < _frame.Cols
                       && row * _frame.Cols + col + span < _cellCount
                       && _cells[row * _frame.Cols + col + span].Bg == bg)
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
                if (cell.Ch == 0 || cell.Width == 0)
                {
                    col++;
                    continue;
                }

                var fg = cell.Fg;
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
                    if (c.Ch == 0 || c.Fg != fg)
                    {
                        break;
                    }
                    run.Append(char.ConvertFromUtf32((int)c.Ch));
                    col++;
                }

                if (run.Length > 0)
                {
                    ds.DrawText(
                        run.ToString(),
                        start * _cellWidth,
                        y,
                        fg == CmuxNative.NoColor ? defaultFg : FromPacked(fg, defaultFg),
                        _format);
                }
            }
        }

        if (_frame.CursorVisible != 0)
        {
            ds.FillRectangle(
                new Rect(_frame.CursorCol * _cellWidth, _frame.CursorRow * _cellHeight, _cellWidth, _cellHeight),
                defaultFg);
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        // Printable input, including anything produced by an IME.
        Send(Encoding.UTF8.GetBytes(args.Character.ToString()));
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys that never arrive as characters.
        byte[]? bytes = e.Key switch
        {
            VirtualKey.Enter => new byte[] { 0x0d },
            VirtualKey.Back => new byte[] { 0x7f },
            VirtualKey.Tab => new byte[] { 0x09 },
            VirtualKey.Escape => new byte[] { 0x1b },
            VirtualKey.Up => Encoding.ASCII.GetBytes("\x1b[A"),
            VirtualKey.Down => Encoding.ASCII.GetBytes("\x1b[B"),
            VirtualKey.Right => Encoding.ASCII.GetBytes("\x1b[C"),
            VirtualKey.Left => Encoding.ASCII.GetBytes("\x1b[D"),
            VirtualKey.Home => Encoding.ASCII.GetBytes("\x1b[H"),
            VirtualKey.End => Encoding.ASCII.GetBytes("\x1b[F"),
            VirtualKey.Delete => Encoding.ASCII.GetBytes("\x1b[3~"),
            VirtualKey.PageUp => Encoding.ASCII.GetBytes("\x1b[5~"),
            VirtualKey.PageDown => Encoding.ASCII.GetBytes("\x1b[6~"),
            _ => null,
        };

        if (bytes is not null)
        {
            Send(bytes);
            e.Handled = true;
        }
    }

    private void Send(byte[] bytes)
    {
        if (_session != IntPtr.Zero && bytes.Length > 0)
        {
            CmuxNative.SessionWrite(_session, bytes, (nuint)bytes.Length);
        }
    }

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
        AppSettings.Changed -= OnSettingsChanged;
        _timer.Stop();
        if (_session != IntPtr.Zero)
        {
            CmuxNative.SessionFree(_session);
            _session = IntPtr.Zero;
        }
        _canvas.RemoveFromVisualTree();
    }
}
