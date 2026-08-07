//! C ABI over the cmux terminal engine.
//!
//! Native frontends that are not written in Rust — the WinUI 3 shell on
//! Windows — drive the same `cmux-tui-core` mux the TUI uses through this
//! surface. The boundary deliberately exposes a flat cell grid rather than
//! engine types, so the ABI stays stable while the engine evolves.
//!
//! Every entry point is null-checked and returns a negative code on failure
//! instead of unwinding, because unwinding across the ABI is undefined.

use std::sync::Arc;

use cmux_tui_core::{Mux, Surface, SurfaceOptions};
use ghostty_vt::{CellWidth, Dirty, RenderState, Rgb};

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
    let (default_fg, default_bg) = built.default_colors;

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
