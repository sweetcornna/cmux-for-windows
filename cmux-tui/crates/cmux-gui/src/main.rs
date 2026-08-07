//! cmux native GUI.
//!
//! A winit window rendering the cmux-tui engine directly with wgpu. The engine
//! is linked in process, so this is the same terminal state the TUI renders,
//! without a control socket in between.

// A GUI build must never open a console. This is unconditional rather than
// release-only: a debug run that spawns a stray terminal window alongside the
// app is exactly the thing this frontend exists to avoid. Diagnostics go to
// CMUX_GUI_LOG instead of stderr.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod gfx;
mod input;
mod term;
mod theme;
mod ui;

use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use gfx::{Gfx, Quad, TextItem};
use ghostty_vt::{Cell, CellWidth, Dirty, RenderFrame};
use ghostty_vt::Rgb;
use term::TerminalSession;
use winit::application::ApplicationHandler;
use winit::event::{ElementState, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::ModifiersState;
use winit::window::{Window, WindowId};

/// PTY output arrives asynchronously and the engine has no GUI wakeup hook yet,
/// so the window polls at roughly display rate. Replacing this with an engine
/// wakeup is the first thing to do after the renderer stabilizes.
const FRAME_INTERVAL: Duration = Duration::from_millis(16);

const DEFAULT_FONT_SIZE: f32 = 15.0;

fn main() -> Result<()> {
    let event_loop = EventLoop::new()?;
    event_loop.set_control_flow(ControlFlow::wait_duration(FRAME_INTERVAL));
    let mut app = App::default();
    event_loop.run_app(&mut app)?;
    if let Some(err) = app.failure {
        return Err(err);
    }
    Ok(())
}

#[derive(Default)]
struct App {
    window: Option<Arc<Window>>,
    gfx: Option<Gfx>,
    term: Option<TerminalSession>,
    mods: ModifiersState,
    next_frame: Option<Instant>,
    failure: Option<anyhow::Error>,
    /// Set when chrome geometry changed, so text is re-shaped even if the
    /// terminal itself reported no damage.
    chrome_dirty: bool,
}

impl App {
    fn start(&mut self, event_loop: &ActiveEventLoop) -> Result<()> {
        let attributes = Window::default_attributes()
            .with_title("cmux")
            .with_inner_size(winit::dpi::LogicalSize::new(1100.0, 700.0));
        let window = Arc::new(event_loop.create_window(attributes)?);

        let font_size = std::env::var("CMUX_GUI_FONT_SIZE")
            .ok()
            .and_then(|v| v.parse::<f32>().ok())
            .filter(|v| *v > 4.0 && *v < 200.0)
            .unwrap_or(DEFAULT_FONT_SIZE);

        apply_dark_titlebar(&window);

        let gfx = Gfx::new(window.clone(), font_size)?;
        let (cols, rows) = gfx.grid_size();
        let term = TerminalSession::new(cols, rows)?;

        self.window = Some(window);
        self.gfx = Some(gfx);
        self.term = Some(term);
        Ok(())
    }

    fn draw(&mut self) -> Result<()> {
        // Read before borrowing the sub-fields mutably.
        let chrome_dirty = self.chrome_dirty;
        let mut reshaped = false;
        let result = self.draw_inner(chrome_dirty, &mut reshaped);
        if reshaped {
            self.chrome_dirty = false;
        }
        result
    }

    fn draw_inner(&mut self, chrome_dirty: bool, reshaped: &mut bool) -> Result<()> {
        let (Some(gfx), Some(term)) = (self.gfx.as_mut(), self.term.as_mut()) else {
            return Ok(());
        };

        let (width, height) = gfx.surface_size();
        let (cell_w, cell_h) = gfx.cell_size();

        // Chrome first: it decides how much room the terminal actually gets.
        let workspaces = term.workspaces();
        let cwd = term.cwd().to_string();
        let rows_model: Vec<ui::SidebarRow> = workspaces
            .iter()
            .map(|workspace| ui::SidebarRow {
                title: workspace.name.clone(),
                subtitle: format!(
                    "{} screen{}",
                    workspace.screens,
                    if workspace.screens == 1 { "" } else { "s" }
                ),
                footer: cwd.clone(),
                badge: None,
                selected: workspace.active,
            })
            .collect();
        let tabs_model = vec![ui::TabItem { title: "PowerShell".into(), active: true }];

        let layout = ui::build(&ui::Chrome {
            rows: &rows_model,
            tabs: &tabs_model,
            width: width as f32,
            height: height as f32,
        });

        // Match the PTY grid to the terminal viewport, not the whole window.
        let cols = (layout.terminal.w / cell_w).floor().max(1.0) as u16;
        let grid_rows = (layout.terminal.h / cell_h).floor().max(1.0) as u16;
        term.resize(cols, grid_rows)?;

        let frame = term.frame()?;
        let (default_fg, _default_bg) = frame.default_colors;

        let mut quads = layout.quads;
        let mut text = layout.text;
        append_terminal(
            &frame,
            layout.terminal,
            cell_w,
            cell_h,
            default_fg,
            &mut quads,
            &mut text,
        );

        // Quads are cheap to rebuild and carry the cursor, so they always
        // refresh. Re-shaping text is not, so it waits for actual damage.
        gfx.set_quads(&quads);
        if frame.dirty != Dirty::Clean || chrome_dirty {
            gfx.set_text(&text);
            *reshaped = true;
        }
        gfx.render([theme::WINDOW_BG[0], theme::WINDOW_BG[1], theme::WINDOW_BG[2]])
    }

    fn fail(&mut self, event_loop: &ActiveEventLoop, err: anyhow::Error) {
        self.failure = Some(err);
        event_loop.exit();
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }
        if let Err(err) = self.start(event_loop) {
            self.fail(event_loop, err);
        }
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _window_id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested => event_loop.exit(),

            WindowEvent::Resized(size) => {
                if let Some(gfx) = self.gfx.as_mut() {
                    gfx.resize(size.width, size.height);
                }
                self.chrome_dirty = true;
                if let Some(window) = &self.window {
                    window.request_redraw();
                }
            }

            WindowEvent::ModifiersChanged(mods) => self.mods = mods.state(),

            WindowEvent::KeyboardInput { event, .. }
                if event.state == ElementState::Pressed =>
            {
                if let Some(bytes) = input::encode(&event, self.mods)
                    && let Some(term) = self.term.as_ref()
                {
                    term.write_input(&bytes);
                }
                if let Some(window) = &self.window {
                    window.request_redraw();
                }
            }

            WindowEvent::RedrawRequested => {
                if let Err(err) = self.draw() {
                    self.fail(event_loop, err);
                }
            }

            _ => {}
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        let now = Instant::now();
        let due = self.next_frame.map(|at| now >= at).unwrap_or(true);
        if due {
            self.next_frame = Some(now + FRAME_INTERVAL);
            if let Some(window) = &self.window {
                window.request_redraw();
            }
        }
        event_loop.set_control_flow(ControlFlow::WaitUntil(
            self.next_frame.unwrap_or(now + FRAME_INTERVAL),
        ));
    }
}

/// Convert a terminal frame into background rectangles and shaped text runs.
///
/// Cells are walked rather than `RenderFrame::runs()` so a column index is
/// always exact: a wide grapheme occupies its lead cell plus a spacer, and both
/// advance the grid by one column each.
fn append_terminal(
    frame: &RenderFrame,
    area: ui::Rect,
    cell_w: f32,
    cell_h: f32,
    default_fg: Rgb,
    quads: &mut Vec<Quad>,
    text: &mut Vec<TextItem>,
) {
    let font_size = cell_h / 1.3;

    for (row_index, row) in frame.styled_rows().iter().enumerate() {
        let y = area.y + row_index as f32 * cell_h;
        if y + cell_h > area.y + area.h {
            break;
        }
        let mut col = 0usize;

        while col < row.len() {
            // Background: coalesce adjacent cells sharing one resolved color.
            let Some(bg) = row[col].resolved_bg else {
                col += 1;
                continue;
            };
            let mut span = 1usize;
            while col + span < row.len() && row[col + span].resolved_bg == Some(bg) {
                span += 1;
            }
            quads.push(Quad::solid(
                area.x + col as f32 * cell_w,
                y,
                span as f32 * cell_w,
                cell_h,
                [bg.r, bg.g, bg.b, 255],
            ));
            col += span;
        }

        // Text: coalesce adjacent cells sharing foreground and emphasis.
        let mut col = 0usize;
        while col < row.len() {
            let cell = &row[col];
            if !is_drawable(cell) {
                col += 1;
                continue;
            }
            let start = col;
            let fg = cell.resolved_fg.unwrap_or(default_fg);
            let bold = cell.bold;
            let italic = cell.italic;
            let mut run_text = String::new();

            while col < row.len() {
                let next = &row[col];
                if next.width == CellWidth::SpacerTail {
                    // Second half of a wide grapheme: consumes a column only.
                    col += 1;
                    continue;
                }
                if !is_drawable(next)
                    || next.resolved_fg.unwrap_or(default_fg) != fg
                    || next.bold != bold
                    || next.italic != italic
                {
                    break;
                }
                run_text.push_str(&next.text);
                col += 1;
            }

            if !run_text.is_empty() {
                text.push(TextItem {
                    text: run_text,
                    x: area.x + start as f32 * cell_w,
                    y,
                    size: font_size,
                    color: [fg.r, fg.g, fg.b, 255],
                    bold,
                    italic,
                    max_width: None,
                });
            }
        }
    }

    if let Some(cursor) = frame.cursor {
        let color = frame.cursor_color.unwrap_or(default_fg);
        quads.push(Quad::solid(
            area.x + cursor.x as f32 * cell_w,
            area.y + cursor.y as f32 * cell_h,
            cell_w,
            cell_h,
            [color.r, color.g, color.b, 255],
        ));
    }
}

/// Blank and invisible cells contribute background only.
fn is_drawable(cell: &Cell) -> bool {
    !cell.invisible && !cell.text.is_empty() && cell.text != " "
}

/// Ask DWM for the dark title bar so the frame matches the app's own chrome.
///
/// Windows otherwise draws a light caption regardless of the system theme,
/// which is the single most obvious tell that a window is not native-looking.
#[cfg(windows)]
fn apply_dark_titlebar(window: &Window) {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};

    let Ok(handle) = window.window_handle() else { return };
    let RawWindowHandle::Win32(win32) = handle.as_raw() else { return };

    const DWMWA_USE_IMMERSIVE_DARK_MODE: u32 = 20;
    let enabled: i32 = 1;
    // SAFETY: `hwnd` comes from a live window we own, and the attribute is the
    // documented BOOL-sized DWMWA_USE_IMMERSIVE_DARK_MODE. A failure here is
    // cosmetic, so the result is intentionally ignored.
    unsafe {
        windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute(
            win32.hwnd.get() as _,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            core::ptr::from_ref(&enabled).cast(),
            core::mem::size_of::<i32>() as u32,
        );
    }
}

#[cfg(not(windows))]
fn apply_dark_titlebar(_window: &Window) {}
