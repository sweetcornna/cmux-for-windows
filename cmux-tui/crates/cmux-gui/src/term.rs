//! The cmux engine behind the GUI: one mux, one PTY surface, one render state.
//!
//! The GUI links `cmux-tui-core` in process rather than speaking the control
//! protocol over a socket, so a frame is a direct `snapshot` of the same
//! terminal state the TUI renders.

use std::sync::Arc;

use anyhow::Result;
use cmux_tui_core::{Mux, Surface, SurfaceOptions};
use ghostty_vt::{RenderFrame, RenderState};

/// A workspace as the sidebar needs to show it.
pub struct WorkspaceInfo {
    pub name: String,
    pub screens: usize,
    pub active: bool,
}

pub struct TerminalSession {
    mux: Arc<Mux>,
    surface: Arc<Surface>,
    render: RenderState,
    cols: u16,
    rows: u16,
    cwd: String,
}

impl TerminalSession {
    pub fn new(cols: u16, rows: u16) -> Result<Self> {
        let mux = Mux::new("cmux-gui", SurfaceOptions::default());
        let cwd = cmux_tui_core::platform::home_dir().map(|p| p.to_string_lossy().into_owned());
        let surface = mux.new_tab(None, cwd.clone(), Some((cols, rows)))?;
        let render = RenderState::new()?;
        Ok(Self {
            mux,
            surface,
            render,
            cols,
            rows,
            cwd: cwd.unwrap_or_else(|| "~".to_string()),
        })
    }

    pub fn cwd(&self) -> &str {
        &self.cwd
    }

    /// Workspaces from the live mux, for the sidebar.
    pub fn workspaces(&self) -> Vec<WorkspaceInfo> {
        self.mux.with_state(|state| {
            state
                .workspaces
                .iter()
                .enumerate()
                .map(|(index, workspace)| WorkspaceInfo {
                    name: if workspace.name.is_empty() {
                        index.to_string()
                    } else {
                        workspace.name.clone()
                    },
                    screens: workspace.screens.len(),
                    active: index == state.active_workspace,
                })
                .collect()
        })
    }

    pub fn grid_size(&self) -> (u16, u16) {
        (self.cols, self.rows)
    }

    /// Pull the latest terminal state and hand back a drawable frame.
    ///
    /// The returned frame's `dirty` reports whether anything actually changed,
    /// so callers can skip re-shaping text on idle ticks.
    pub fn frame(&mut self) -> Result<RenderFrame> {
        self.surface.snapshot(&mut self.render)?;
        let frame = self.render.build_frame()?;
        self.render.set_clean();
        Ok(frame)
    }

    /// Terminal default foreground and background, for the window clear color
    /// and for cells that carry no explicit color.
    pub fn default_colors(&self) -> (ghostty_vt::Rgb, ghostty_vt::Rgb) {
        self.render.default_colors()
    }

    pub fn write_input(&self, bytes: &[u8]) {
        // A dead PTY is normal during shutdown; the window closes on exit.
        let _ = self.surface.write_bytes(bytes);
    }

    pub fn resize(&mut self, cols: u16, rows: u16) -> Result<()> {
        if cols == 0 || rows == 0 || (cols == self.cols && rows == self.rows) {
            return Ok(());
        }
        self.surface.resize(cols, rows)?;
        self.cols = cols;
        self.rows = rows;
        Ok(())
    }
}
