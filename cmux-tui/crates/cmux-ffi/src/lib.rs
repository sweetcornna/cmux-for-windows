//! C ABI over the cmux terminal engine.
//!
//! Native frontends that are not written in Rust — the WinUI 3 shell on
//! Windows — drive the same `cmux-tui-core` mux the TUI uses through this
//! surface. The boundary deliberately exposes a flat cell grid rather than
//! engine types, so the ABI stays stable while the engine evolves.
//!
//! Every entry point is null-checked and returns a negative code on failure
//! instead of unwinding, because unwinding across the ABI is undefined.

pub mod ghostty_config;

use std::sync::Arc;

use cmux_tui_core::{DefaultColors, Mux, Surface, SurfaceOptions};
use ghostty_vt::{CellWidth, Dirty, RenderState, Rgb};

use ghostty_config::GhosttyConfig;

/// Returned when a pointer argument is null.
pub const CMUX_ERR_NULL: i32 = -1;
/// Returned when the engine failed to produce a frame.
pub const CMUX_ERR_ENGINE: i32 = -2;
/// Returned when the caller's cell buffer is too small for the grid.
pub const CMUX_ERR_CAPACITY: i32 = -3;

/// Sentinel for "this cell has no explicit background".
pub const CMUX_NO_COLOR: u32 = 0xFFFF_FFFF;

/// One terminal cell, flattened for cross-language consumption.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct CmuxCell {
    /// Unicode scalar value, or 0 for a blank cell.
    pub ch: u32,
    /// 0x00RRGGBB, or [`CMUX_NO_COLOR`] to use the frame's default.
    pub fg: u32,
    /// 0x00RRGGBB, or [`CMUX_NO_COLOR`] for no explicit background.
    pub bg: u32,
    /// Protocol-v7 attribute bits, matching `ghostty_vt::Cell::attrs`.
    pub attrs: u16,
    /// 1 narrow, 2 the lead of a wide grapheme, 0 a trailing spacer.
    pub width: u8,
    pub _reserved: u8,
}

/// Frame-level state accompanying a cell snapshot.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct CmuxFrame {
    pub cols: u16,
    pub rows: u16,
    pub cursor_col: u16,
    pub cursor_row: u16,
    /// 1 when the cursor should be drawn.
    pub cursor_visible: u8,
    /// 1 when anything changed since the previous snapshot.
    pub dirty: u8,
    pub _reserved: [u8; 2],
    pub default_fg: u32,
    pub default_bg: u32,
}

/// Presentation settings read from the user's Ghostty config.
///
/// The engine already resolves cell colors through the theme palette, so this
/// is what a frontend needs for the parts the engine does not draw: the window
/// surface behind the grid, and the font to shape glyphs with.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct CmuxTheme {
    pub background: u32,
    pub foreground: u32,
    pub cursor: u32,
    pub selection_background: u32,
    pub selection_foreground: u32,
    pub font_size: f32,
    /// NUL-terminated UTF-8; empty when the config names no font.
    pub font_family: [u8; 128],
    /// 1 when a Ghostty config file was actually found.
    pub loaded: u8,
    pub _reserved: [u8; 3],
}

/// Read the user's Ghostty appearance settings.
///
/// Always fills `out` with usable values: Ghostty's own defaults when no config
/// file exists. Returns 0 on success.
///
/// # Safety
/// `out` must point to a writable [`CmuxTheme`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_theme_load(out: *mut CmuxTheme) -> i32 {
    if out.is_null() {
        return CMUX_ERR_NULL;
    }
    let config = GhosttyConfig::load();

    let mut font_family = [0_u8; 128];
    if let Some(name) = config.font_family.as_deref() {
        let bytes = name.as_bytes();
        // Leave room for the NUL, and never split a UTF-8 sequence.
        let mut len = bytes.len().min(font_family.len() - 1);
        while len > 0 && !name.is_char_boundary(len) {
            len -= 1;
        }
        font_family[..len].copy_from_slice(&bytes[..len]);
    }

    unsafe {
        *out = CmuxTheme {
            background: pack(config.background.unwrap_or(ghostty_config::DEFAULT_BACKGROUND)),
            foreground: pack(config.foreground.unwrap_or(ghostty_config::DEFAULT_FOREGROUND)),
            cursor: config.cursor_color.map(pack).unwrap_or(CMUX_NO_COLOR),
            selection_background: config.selection_background.map(pack).unwrap_or(CMUX_NO_COLOR),
            selection_foreground: config.selection_foreground.map(pack).unwrap_or(CMUX_NO_COLOR),
            font_size: config.font_size.unwrap_or(0.0),
            font_family,
            loaded: u8::from(config.source.is_some()),
            _reserved: [0; 3],
        };
    }
    0
}

/// Opaque handle owned by the caller.
pub struct CmuxSession {
    _mux: Arc<Mux>,
    surface: Arc<Surface>,
    render: RenderState,
    cols: u16,
    rows: u16,
}

fn pack(color: Rgb) -> u32 {
    ((color.r as u32) << 16) | ((color.g as u32) << 8) | color.b as u32
}

/// Create a session with one PTY running the platform's default shell.
///
/// Returns null on failure. The result must be released with
/// [`cmux_session_free`].
#[unsafe(no_mangle)]
pub extern "C" fn cmux_session_new(cols: u16, rows: u16) -> *mut CmuxSession {
    let cols = cols.max(1);
    let rows = rows.max(1);
    let mux = Mux::new("cmux-gui", SurfaceOptions::default());
    let cwd = cmux_tui_core::platform::home_dir().map(|p| p.to_string_lossy().into_owned());
    let Ok(surface) = mux.new_tab(None, cwd, Some((cols, rows))) else {
        return std::ptr::null_mut();
    };

    // Adopt the user's Ghostty appearance, so cells resolve through the theme
    // palette. This must come after the surface exists and go through the
    // surface rather than the mux: `Mux::set_default_colors` only walks
    // surfaces that already exist, and short-circuits when the stored value is
    // unchanged, so seeding it earlier would silently apply to nothing.
    let config = GhosttyConfig::load();
    surface.set_default_colors(DefaultColors {
        fg: Some(config.foreground.unwrap_or(ghostty_config::DEFAULT_FOREGROUND)),
        bg: Some(config.background.unwrap_or(ghostty_config::DEFAULT_BACKGROUND)),
        cursor: config.cursor_color,
        selection_bg: config.selection_background,
        selection_fg: config.selection_foreground,
        palette: config.palette,
        ..DefaultColors::default()
    });
    let Ok(render) = RenderState::new() else {
        return std::ptr::null_mut();
    };
    Box::into_raw(Box::new(CmuxSession { _mux: mux, surface, render, cols, rows }))
}

/// Release a session created by [`cmux_session_new`].
///
/// # Safety
/// `session` must be a pointer returned by [`cmux_session_new`] that has not
/// already been freed, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_free(session: *mut CmuxSession) {
    if session.is_null() {
        return;
    }
    drop(unsafe { Box::from_raw(session) });
}

/// Send bytes to the PTY. Returns 0 on success.
///
/// # Safety
/// `session` must be live, and `bytes` must point to at least `len` readable
/// bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_write(
    session: *mut CmuxSession,
    bytes: *const u8,
    len: usize,
) -> i32 {
    if session.is_null() || (bytes.is_null() && len != 0) {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };
    let slice = if len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(bytes, len) } };
    match session.surface.write_bytes(slice) {
        Ok(()) => 0,
        Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Resize the PTY grid. Returns 0 on success.
///
/// # Safety
/// `session` must be a live session pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_resize(
    session: *mut CmuxSession,
    cols: u16,
    rows: u16,
) -> i32 {
    if session.is_null() || cols == 0 || rows == 0 {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };
    if session.cols == cols && session.rows == rows {
        return 0;
    }
    match session.surface.resize(cols, rows) {
        Ok(_) => {
            session.cols = cols;
            session.rows = rows;
            0
        }
        Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Apply a Ghostty-format theme to a live session.
///
/// `text` is the contents of a theme file, not a path: the frontend owns
/// theme discovery (bundled assets plus the user's `themes` directory) and
/// this side owns parsing, so the ABI never grows a filesystem layout.
///
/// Keys absent from the theme fall back to the user's Ghostty config, so
/// switching themes does not silently discard a configured font or cursor.
///
/// # Safety
/// `session` must be live, and `text` must point to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_apply_theme_text(
    session: *mut CmuxSession,
    text: *const u8,
    len: usize,
) -> i32 {
    if session.is_null() || (text.is_null() && len != 0) {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };
    let bytes = if len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(text, len) } };
    let Ok(text) = std::str::from_utf8(bytes) else {
        return CMUX_ERR_ENGINE;
    };

    // Start from the user's config so a theme that only sets colours keeps
    // their font and anything else it does not mention.
    let mut config = GhosttyConfig::load();
    config.apply(text);

    session.surface.set_default_colors(DefaultColors {
        fg: Some(config.foreground.unwrap_or(ghostty_config::DEFAULT_FOREGROUND)),
        bg: Some(config.background.unwrap_or(ghostty_config::DEFAULT_BACKGROUND)),
        cursor: config.cursor_color,
        selection_bg: config.selection_background,
        selection_fg: config.selection_foreground,
        palette: config.palette,
        ..DefaultColors::default()
    });
    0
}

/// Copy the current grid into `cells` and frame state into `frame`.
///
/// Returns the number of cells written, or a negative error code. Cells are
/// row-major, `cols * rows` of them.
///
/// # Safety
/// `session` and `frame` must be valid, and `cells` must point to at least
/// `capacity` writable [`CmuxCell`] values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_snapshot(
    session: *mut CmuxSession,
    cells: *mut CmuxCell,
    capacity: usize,
    frame: *mut CmuxFrame,
) -> i32 {
    if session.is_null() || cells.is_null() || frame.is_null() {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };

    if session.surface.snapshot(&mut session.render).is_err() {
        return CMUX_ERR_ENGINE;
    }
    let Ok(built) = session.render.build_frame() else {
        return CMUX_ERR_ENGINE;
    };
    session.render.set_clean();

    let rows = built.styled_rows();
    let grid_rows = rows.len();
    let grid_cols = rows.first().map(|r| r.len()).unwrap_or(0);
    let needed = grid_cols * grid_rows;
    if needed > capacity {
        return CMUX_ERR_CAPACITY;
    }

    let out = unsafe { std::slice::from_raw_parts_mut(cells, needed) };
    // `default_colors()` returns (background, foreground), in that order.
    let (default_bg, default_fg) = built.default_colors;

    for (row_index, row) in rows.iter().enumerate() {
        for (col_index, cell) in row.iter().enumerate() {
            let index = row_index * grid_cols + col_index;
            if index >= needed {
                break;
            }
            out[index] = CmuxCell {
                ch: cell.text.chars().next().map(u32::from).unwrap_or(0),
                fg: cell.resolved_fg.map(pack).unwrap_or(CMUX_NO_COLOR),
                bg: cell.resolved_bg.map(pack).unwrap_or(CMUX_NO_COLOR),
                attrs: cell.attrs(),
                width: match cell.width {
                    CellWidth::Wide => 2,
                    CellWidth::SpacerTail | CellWidth::SpacerHead => 0,
                    CellWidth::Narrow => 1,
                },
                _reserved: 0,
            };
        }
    }

    let cursor = built.cursor;
    unsafe {
        *frame = CmuxFrame {
            cols: grid_cols as u16,
            rows: grid_rows as u16,
            cursor_col: cursor.map(|c| c.x).unwrap_or(0),
            cursor_row: cursor.map(|c| c.y).unwrap_or(0),
            cursor_visible: u8::from(cursor.is_some()),
            dirty: u8::from(built.dirty != Dirty::Clean),
            _reserved: [0; 2],
            default_fg: pack(default_fg),
            default_bg: pack(default_bg),
        };
    }

    needed as i32
}
