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

use std::sync::{Arc, Mutex};

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

/// One application-level persistent mux shared by all native workspace views.
pub struct CmuxMux {
    mux: Arc<Mux>,
    last_error: Mutex<String>,
}

impl CmuxMux {
    fn record_error(&self, error: &impl std::fmt::Display) -> i32 {
        *self.last_error.lock().unwrap() = format!("{error:#}");
        CMUX_ERR_ENGINE
    }

    fn clear_error(&self) {
        self.last_error.lock().unwrap().clear();
    }
}

/// Workspace metadata copied into caller-owned memory.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct CmuxWorkspace {
    pub id: u64,
    pub active: u8,
    pub _reserved: [u8; 7],
    pub name_utf8: [u8; 256],
    pub public_id_utf8: [u8; 64],
}

/// Opaque terminal view handle owned by the caller.
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

unsafe fn optional_utf8(value: *const u8, len: usize) -> Option<String> {
    if value.is_null() || len == 0 {
        return None;
    }
    let bytes = unsafe { std::slice::from_raw_parts(value, len) };
    std::str::from_utf8(bytes)
        .ok()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
}

unsafe fn required_utf8(value: *const u8, len: usize) -> Result<String, i32> {
    if value.is_null() || len == 0 {
        return Err(CMUX_ERR_NULL);
    }
    unsafe { optional_utf8(value, len) }.ok_or(CMUX_ERR_ENGINE)
}

fn copy_utf8<const N: usize>(value: &str) -> [u8; N] {
    let mut output = [0; N];
    let bytes = value.as_bytes();
    let mut len = bytes.len().min(N.saturating_sub(1));
    while len > 0 && !value.is_char_boundary(len) {
        len -= 1;
    }
    output[..len].copy_from_slice(&bytes[..len]);
    output
}

fn session_from_surface(
    mux: Arc<Mux>,
    surface: Arc<Surface>,
    cols: u16,
    rows: u16,
) -> *mut CmuxSession {
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

/// Open the persistent mux used by the Windows GUI.
#[unsafe(no_mangle)]
pub extern "C" fn cmux_mux_open() -> *mut CmuxMux {
    let Some(root) = cmux_tui_core::platform::workspace_state_dir() else {
        return std::ptr::null_mut();
    };
    match Mux::open_persistent("cmux-gui", SurfaceOptions::default(), &root) {
        Ok(mux) => Box::into_raw(Box::new(CmuxMux { mux, last_error: Mutex::new(String::new()) })),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Release a mux created by [`cmux_mux_open`].
///
/// # Safety
/// `mux` must be a live pointer returned by [`cmux_mux_open`], or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_free(mux: *mut CmuxMux) {
    if !mux.is_null() {
        drop(unsafe { Box::from_raw(mux) });
    }
}

/// Copy the most recent mux operation error as UTF-8.
///
/// Calling with a null `buffer` and zero `capacity` returns the required byte
/// count. The error is retained until a later operation succeeds.
///
/// # Safety
/// `mux` must be live. A non-null `buffer` must be writable for `capacity` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_last_error(
    mux: *mut CmuxMux,
    buffer: *mut u8,
    capacity: usize,
) -> i32 {
    if mux.is_null() || (buffer.is_null() && capacity != 0) {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    let error = mux.last_error.lock().unwrap();
    let bytes = error.as_bytes();
    let Ok(written) = i32::try_from(bytes.len()) else {
        return CMUX_ERR_ENGINE;
    };
    if buffer.is_null() {
        return written;
    }
    if bytes.len() > capacity {
        return CMUX_ERR_CAPACITY;
    }
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, bytes.len()) };
    written
}

/// Return the number of durable workspaces, or a negative error code.
///
/// # Safety
/// `mux` must be a live mux pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_count(mux: *mut CmuxMux) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    i32::try_from(mux.mux.with_state(|state| state.workspaces.len())).unwrap_or(CMUX_ERR_ENGINE)
}

/// Copy one ordered workspace record into `out`.
///
/// # Safety
/// `mux` must be live and `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_get(
    mux: *mut CmuxMux,
    index: usize,
    out: *mut CmuxWorkspace,
) -> i32 {
    if mux.is_null() || out.is_null() {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    let Some(workspace) = mux.mux.with_state(|state| {
        state.workspaces.get(index).map(|workspace| CmuxWorkspace {
            id: workspace.id,
            active: u8::from(index == state.active_workspace),
            _reserved: [0; 7],
            name_utf8: copy_utf8(&workspace.name),
            public_id_utf8: copy_utf8(workspace.public_id.as_str()),
        })
    }) else {
        return CMUX_ERR_ENGINE;
    };
    unsafe { *out = workspace };
    0
}

/// Add an empty durable workspace and return its numeric id, or zero on failure.
///
/// # Safety
/// `name` must point to `name_len` bytes of UTF-8, or be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_create(
    mux: *mut CmuxMux,
    name: *const u8,
    name_len: usize,
) -> u64 {
    if mux.is_null() || (name.is_null() && name_len != 0) {
        return 0;
    }
    let mux = unsafe { &*mux };
    let name = unsafe { optional_utf8(name, name_len) };
    mux.mux
        .create_empty_workspace(name, None, None)
        .map(|placement| placement.workspace)
        .unwrap_or(0)
}

/// Persist the selected workspace.
///
/// # Safety
/// `mux` must be a live mux pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_select(mux: *mut CmuxMux, workspace: u64) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    let Some(index) = mux.mux.with_state(|state| {
        state.workspaces.iter().position(|candidate| candidate.id == workspace)
    }) else {
        return CMUX_ERR_ENGINE;
    };
    mux.mux.select_workspace(Some(index), None);
    if mux.mux.with_state(|state| state.active_workspace == index) { 0 } else { CMUX_ERR_ENGINE }
}

/// Close a workspace and all of its durable resources.
///
/// # Safety
/// `mux` must be a live mux pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_close(mux: *mut CmuxMux, workspace: u64) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    if mux.mux.close_workspace(workspace) { 0 } else { CMUX_ERR_ENGINE }
}

/// Open the active terminal view for a workspace.
///
/// Empty workspaces use `cwd`; restored terminals always start a fresh default
/// shell in the user's home directory.
///
/// # Safety
/// `mux` must be live and `cwd` must point to `cwd_len` bytes of UTF-8, or be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_open(
    mux: *mut CmuxMux,
    workspace: u64,
    cols: u16,
    rows: u16,
    cwd: *const u8,
    cwd_len: usize,
) -> *mut CmuxSession {
    if mux.is_null() || (cwd.is_null() && cwd_len != 0) {
        return std::ptr::null_mut();
    }
    let mux = unsafe { &*mux };
    let cwd =
        unsafe { optional_utf8(cwd, cwd_len) }.filter(|path| std::path::Path::new(path).is_dir());
    let cols = cols.max(1);
    let rows = rows.max(1);
    let Ok(surface) = mux.mux.open_workspace_terminal(workspace, cwd, Some((cols, rows))) else {
        return std::ptr::null_mut();
    };
    session_from_surface(mux.mux.clone(), surface, cols, rows)
}

/// Copy the authoritative public mux snapshot as UTF-8 JSON.
///
/// Calling with a null `buffer` and zero `capacity` returns the required byte
/// count. A caller that loses a size race receives [`CMUX_ERR_CAPACITY`] and
/// should query the size again.
///
/// # Safety
/// `mux` must be live. A non-null `buffer` must be writable for `capacity` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_snapshot_json(
    mux: *mut CmuxMux,
    buffer: *mut u8,
    capacity: usize,
) -> i32 {
    if mux.is_null() || (buffer.is_null() && capacity != 0) {
        return CMUX_ERR_NULL;
    }
    let mux = unsafe { &*mux };
    let Ok(snapshot) = mux.mux.public_session_snapshot_json() else {
        return CMUX_ERR_ENGINE;
    };
    let bytes = snapshot.as_bytes();
    let Ok(written) = i32::try_from(bytes.len()) else {
        return CMUX_ERR_ENGINE;
    };
    if buffer.is_null() {
        return written;
    }
    if bytes.len() > capacity {
        return CMUX_ERR_CAPACITY;
    }
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, bytes.len()) };
    written
}

/// Open one terminal tab by stable public id.
///
/// # Safety
/// `mux` must be live and `tab` must point to `tab_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_tab_open(
    mux: *mut CmuxMux,
    tab: *const u8,
    tab_len: usize,
    cols: u16,
    rows: u16,
) -> *mut CmuxSession {
    if mux.is_null() {
        return std::ptr::null_mut();
    }
    let Ok(tab) = (unsafe { required_utf8(tab, tab_len) }) else {
        return std::ptr::null_mut();
    };
    let mux = unsafe { &*mux };
    let cols = cols.max(1);
    let rows = rows.max(1);
    let Ok(surface) = mux.mux.open_public_terminal_tab(&tab, Some((cols, rows))) else {
        return std::ptr::null_mut();
    };
    session_from_surface(mux.mux.clone(), surface, cols, rows)
}

/// Add a fresh default-shell terminal to a workspace addressed by public id.
///
/// # Safety
/// String pointers must reference their declared UTF-8 byte lengths. `cwd` may
/// be null when `cwd_len` is zero.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_workspace_create_terminal(
    mux: *mut CmuxMux,
    workspace: *const u8,
    workspace_len: usize,
    cwd: *const u8,
    cwd_len: usize,
) -> i32 {
    if mux.is_null() || (cwd.is_null() && cwd_len != 0) {
        return CMUX_ERR_NULL;
    }
    let Ok(workspace) = (unsafe { required_utf8(workspace, workspace_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let cwd =
        unsafe { optional_utf8(cwd, cwd_len) }.filter(|path| std::path::Path::new(path).is_dir());
    let mux = unsafe { &*mux };
    match mux.mux.create_terminal_in_public_workspace(&workspace, cwd, None) {
        Ok(_) => 0,
        Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Add a terminal tab to a pane addressed by public id.
///
/// # Safety
/// `pane` must point to `pane_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_pane_create_terminal(
    mux: *mut CmuxMux,
    pane: *const u8,
    pane_len: usize,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(pane) = (unsafe { required_utf8(pane, pane_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let mux = unsafe { &*mux };
    match mux.mux.create_terminal_tab_in_public_pane(&pane, None) {
        Ok(_) => {
            mux.clear_error();
            0
        }
        Err(error) => mux.record_error(&error),
    }
}

/// Split a pane and create a terminal in the new pane.
///
/// `direction` is zero for right and one for down.
///
/// # Safety
/// `pane` must point to `pane_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_pane_split(
    mux: *mut CmuxMux,
    pane: *const u8,
    pane_len: usize,
    direction: u8,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(pane) = (unsafe { required_utf8(pane, pane_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let direction = match direction {
        0 => cmux_tui_core::SplitDir::Right,
        1 => cmux_tui_core::SplitDir::Down,
        _ => return CMUX_ERR_ENGINE,
    };
    let mux = unsafe { &*mux };
    match mux.mux.split_public_pane(&pane, direction, None) {
        Ok(_) => {
            mux.clear_error();
            0
        }
        Err(error) => mux.record_error(&error),
    }
}

/// Focus a pane addressed by public id.
///
/// # Safety
/// `pane` must point to `pane_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_pane_focus(
    mux: *mut CmuxMux,
    pane: *const u8,
    pane_len: usize,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(pane) = (unsafe { required_utf8(pane, pane_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let mux = unsafe { &*mux };
    match mux.mux.focus_public_pane(&pane) {
        Ok(true) => 0,
        Ok(false) | Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Close a pane addressed by public id.
///
/// # Safety
/// `pane` must point to `pane_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_pane_close(
    mux: *mut CmuxMux,
    pane: *const u8,
    pane_len: usize,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(pane) = (unsafe { required_utf8(pane, pane_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let mux = unsafe { &*mux };
    match mux.mux.close_public_pane(&pane) {
        Ok(true) => 0,
        Ok(false) | Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Select a tab addressed by public id.
///
/// # Safety
/// `tab` must point to `tab_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_tab_select(
    mux: *mut CmuxMux,
    tab: *const u8,
    tab_len: usize,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(tab) = (unsafe { required_utf8(tab, tab_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let mux = unsafe { &*mux };
    match mux.mux.select_public_tab(&tab) {
        Ok(true) => 0,
        Ok(false) | Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Close a tab addressed by public id.
///
/// # Safety
/// `tab` must point to `tab_len` bytes of UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_mux_tab_close(
    mux: *mut CmuxMux,
    tab: *const u8,
    tab_len: usize,
) -> i32 {
    if mux.is_null() {
        return CMUX_ERR_NULL;
    }
    let Ok(tab) = (unsafe { required_utf8(tab, tab_len) }) else {
        return CMUX_ERR_ENGINE;
    };
    let mux = unsafe { &*mux };
    match mux.mux.close_public_tab(&tab) {
        Ok(true) => 0,
        Ok(false) | Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Create a session with one PTY running the platform's default shell.
///
/// Returns null on failure. The result must be released with
/// [`cmux_session_free`].
#[unsafe(no_mangle)]
pub extern "C" fn cmux_session_new(cols: u16, rows: u16) -> *mut CmuxSession {
    session_new(cols, rows, None)
}

/// Create a session whose shell starts in `cwd`.
///
/// A separate entry point rather than a changed signature, so the existing one
/// stays ABI-stable. An unreadable or non-existent directory falls back to the
/// home directory instead of failing the launch, since the caller is usually
/// Explorer handing over whatever folder was right-clicked.
///
/// # Safety
/// `cwd` must point to `cwd_len` readable bytes of UTF-8, or be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_new_in(
    cols: u16,
    rows: u16,
    cwd: *const u8,
    cwd_len: usize,
) -> *mut CmuxSession {
    let directory = if cwd.is_null() || cwd_len == 0 {
        None
    } else {
        let bytes = unsafe { std::slice::from_raw_parts(cwd, cwd_len) };
        std::str::from_utf8(bytes)
            .ok()
            .map(str::trim)
            .filter(|path| !path.is_empty() && std::path::Path::new(path).is_dir())
            .map(str::to_owned)
    };
    session_new(cols, rows, directory)
}

fn session_new(cols: u16, rows: u16, cwd: Option<String>) -> *mut CmuxSession {
    let cols = cols.max(1);
    let rows = rows.max(1);
    let mux = Mux::new("cmux-gui", SurfaceOptions::default());
    let cwd = cwd
        .or_else(|| cmux_tui_core::platform::home_dir().map(|p| p.to_string_lossy().into_owned()));
    let Ok(surface) = mux.new_tab(None, cwd, Some((cols, rows))) else {
        return std::ptr::null_mut();
    };

    session_from_surface(mux, surface, cols, rows)
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

/// Scroll the viewport by `delta_rows`, negative for older output.
///
/// Returns 0 on success. Scrolling is a view operation, so it does not disturb
/// the PTY or what the shell believes the screen contains.
///
/// # Safety
/// `session` must be a live session pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_scroll(session: *mut CmuxSession, delta_rows: i32) -> i32 {
    if session.is_null() {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };
    match session.surface.view_scroll_delta(delta_rows as isize) {
        Ok(_) => 0,
        Err(_) => CMUX_ERR_ENGINE,
    }
}

/// Jump the viewport back to the live bottom.
///
/// # Safety
/// `session` must be a live session pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cmux_session_scroll_to_bottom(session: *mut CmuxSession) -> i32 {
    if session.is_null() {
        return CMUX_ERR_NULL;
    }
    let session = unsafe { &mut *session };
    match session.surface.view_scroll_to_bottom() {
        Ok(_) => 0,
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
