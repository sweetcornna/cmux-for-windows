//! Colors and metrics matching the cmux app's chrome.
//!
//! Values are read off the published cmux screenshots rather than invented, so
//! the Windows frontend reads as the same product rather than a lookalike.

pub type Rgba = [u8; 4];

pub const fn rgb(r: u8, g: u8, b: u8) -> Rgba {
    [r, g, b, 255]
}

pub const fn rgba(r: u8, g: u8, b: u8, a: u8) -> Rgba {
    [r, g, b, a]
}

/// Window background behind everything.
pub const WINDOW_BG: Rgba = rgb(0x14, 0x14, 0x16);
/// Session sidebar surface.
pub const SIDEBAR_BG: Rgba = rgb(0x17, 0x18, 0x1A);
/// Selected session row: the cmux accent blue.
pub const ACCENT: Rgba = rgb(0x0A, 0x6C, 0xFF);
/// Hairline separating sidebar from content, and under the tab strip.
pub const BORDER: Rgba = rgb(0x2A, 0x2B, 0x2F);
/// Tab strip surface.
pub const TABBAR_BG: Rgba = rgb(0x1B, 0x1C, 0x1F);
/// Active tab surface.
pub const TAB_ACTIVE_BG: Rgba = rgb(0x26, 0x27, 0x2B);
/// Terminal surface, used when the engine reports no explicit background.
pub const TERMINAL_BG: Rgba = rgb(0x1B, 0x1B, 0x1D);

pub const TEXT_PRIMARY: Rgba = rgb(0xE8, 0xE8, 0xEA);
pub const TEXT_SECONDARY: Rgba = rgb(0x9A, 0x9A, 0xA0);
pub const TEXT_TERTIARY: Rgba = rgb(0x6E, 0x6E, 0x76);
/// Text on top of the accent fill.
pub const TEXT_ON_ACCENT: Rgba = rgb(0xFF, 0xFF, 0xFF);
/// Notification ring and badge fill.
pub const BADGE: Rgba = rgb(0x0A, 0x84, 0xFF);

/// Focused pane ring, the "notification ring" in cmux terms.
pub const RING: Rgba = rgba(0x0A, 0x84, 0xFF, 0xCC);

pub const SIDEBAR_WIDTH: f32 = 300.0;
pub const SIDEBAR_ROW_PADDING_X: f32 = 12.0;
pub const SIDEBAR_ROW_PADDING_Y: f32 = 9.0;
pub const SIDEBAR_ROW_GAP: f32 = 2.0;
pub const SIDEBAR_ROW_RADIUS: f32 = 7.0;
pub const SIDEBAR_HEADER_HEIGHT: f32 = 40.0;

pub const TABBAR_HEIGHT: f32 = 36.0;
pub const TAB_RADIUS: f32 = 7.0;
pub const TAB_PADDING_X: f32 = 12.0;
pub const TAB_MIN_WIDTH: f32 = 120.0;
pub const TAB_MAX_WIDTH: f32 = 260.0;

pub const TITLE_SIZE: f32 = 13.0;
pub const BODY_SIZE: f32 = 12.0;
pub const FOOTER_SIZE: f32 = 11.0;

pub const CONTENT_PADDING: f32 = 8.0;
