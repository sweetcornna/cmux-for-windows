//! Chrome layout: session sidebar, tab strip, and the terminal viewport.
//!
//! Layout is computed from a plain view-model rather than engine types so the
//! geometry can be reasoned about (and later tested) without standing up a mux.

use crate::gfx::{Quad, TextItem};
use crate::theme;

/// One session row in the left sidebar.
pub struct SidebarRow {
    pub title: String,
    pub subtitle: String,
    pub footer: String,
    /// Unread notification count, drawn as the blue badge cmux uses.
    pub badge: Option<u32>,
    pub selected: bool,
}

/// One tab in the strip above the terminal.
pub struct TabItem {
    pub title: String,
    pub active: bool,
}

pub struct Chrome<'a> {
    pub rows: &'a [SidebarRow],
    pub tabs: &'a [TabItem],
    pub width: f32,
    pub height: f32,
}

/// Pixel rectangle handed back to the caller for terminal placement.
#[derive(Clone, Copy)]
pub struct Rect {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
}

pub struct Layout {
    pub quads: Vec<Quad>,
    pub text: Vec<TextItem>,
    /// Where the terminal grid should be drawn.
    pub terminal: Rect,
}

pub fn build(chrome: &Chrome) -> Layout {
    let mut quads = Vec::new();
    let mut text = Vec::new();

    let sidebar_w = theme::SIDEBAR_WIDTH.min(chrome.width * 0.4);

    // Window and sidebar surfaces.
    quads.push(Quad::solid(0.0, 0.0, chrome.width, chrome.height, theme::WINDOW_BG));
    quads.push(Quad::solid(0.0, 0.0, sidebar_w, chrome.height, theme::SIDEBAR_BG));
    // Hairline divider, drawn as a 1px quad so it survives fractional scaling.
    quads.push(Quad::solid(sidebar_w, 0.0, 1.0, chrome.height, theme::BORDER));

    text.push(TextItem {
        text: "workspaces".into(),
        x: theme::SIDEBAR_ROW_PADDING_X,
        y: 12.0,
        size: theme::FOOTER_SIZE,
        color: theme::TEXT_TERTIARY,
        bold: false,
        italic: false,
        max_width: Some(sidebar_w - theme::SIDEBAR_ROW_PADDING_X * 2.0),
    });

    // Sidebar rows.
    let row_h = theme::SIDEBAR_ROW_PADDING_Y * 2.0 + 46.0;
    let mut y = theme::SIDEBAR_HEADER_HEIGHT;
    for row in chrome.rows {
        if y + row_h > chrome.height {
            break;
        }
        let inset = 6.0;
        if row.selected {
            quads.push(Quad::rounded(
                inset,
                y,
                sidebar_w - inset * 2.0,
                row_h,
                theme::ACCENT,
                theme::SIDEBAR_ROW_RADIUS,
            ));
        }

        let (title_color, body_color, footer_color) = if row.selected {
            (theme::TEXT_ON_ACCENT, theme::TEXT_ON_ACCENT, theme::TEXT_ON_ACCENT)
        } else {
            (theme::TEXT_PRIMARY, theme::TEXT_SECONDARY, theme::TEXT_TERTIARY)
        };

        let mut text_x = inset + theme::SIDEBAR_ROW_PADDING_X;
        let text_w = sidebar_w - text_x - theme::SIDEBAR_ROW_PADDING_X - inset;

        // Notification badge sits left of the title, like the app's sidebar.
        if let Some(count) = row.badge {
            let d = 16.0;
            quads.push(Quad::rounded(
                text_x,
                y + theme::SIDEBAR_ROW_PADDING_Y + 1.0,
                d,
                d,
                if row.selected { theme::TEXT_ON_ACCENT } else { theme::BADGE },
                d / 2.0,
            ));
            text.push(TextItem {
                text: count.to_string(),
                x: text_x + 5.0,
                y: y + theme::SIDEBAR_ROW_PADDING_Y + 2.0,
                size: 10.0,
                color: if row.selected { theme::ACCENT } else { theme::TEXT_ON_ACCENT },
                bold: true,
                italic: false,
                max_width: Some(d),
            });
            text_x += d + 7.0;
        }

        text.push(TextItem {
            text: row.title.clone(),
            x: text_x,
            y: y + theme::SIDEBAR_ROW_PADDING_Y,
            size: theme::TITLE_SIZE,
            color: title_color,
            bold: true,
            italic: false,
            max_width: Some(text_w),
        });
        text.push(TextItem {
            text: row.subtitle.clone(),
            x: inset + theme::SIDEBAR_ROW_PADDING_X,
            y: y + theme::SIDEBAR_ROW_PADDING_Y + 18.0,
            size: theme::BODY_SIZE,
            color: body_color,
            bold: false,
            italic: false,
            max_width: Some(text_w),
        });
        text.push(TextItem {
            text: row.footer.clone(),
            x: inset + theme::SIDEBAR_ROW_PADDING_X,
            y: y + theme::SIDEBAR_ROW_PADDING_Y + 34.0,
            size: theme::FOOTER_SIZE,
            color: footer_color,
            bold: false,
            italic: false,
            max_width: Some(text_w),
        });

        y += row_h + theme::SIDEBAR_ROW_GAP;
    }

    // Tab strip.
    let content_x = sidebar_w + 1.0;
    let content_w = (chrome.width - content_x).max(0.0);
    quads.push(Quad::solid(content_x, 0.0, content_w, theme::TABBAR_HEIGHT, theme::TABBAR_BG));
    quads.push(Quad::solid(
        content_x,
        theme::TABBAR_HEIGHT - 1.0,
        content_w,
        1.0,
        theme::BORDER,
    ));

    if !chrome.tabs.is_empty() {
        let available = content_w - theme::CONTENT_PADDING * 2.0;
        let per = (available / chrome.tabs.len() as f32)
            .clamp(theme::TAB_MIN_WIDTH, theme::TAB_MAX_WIDTH);
        let mut tab_x = content_x + theme::CONTENT_PADDING;
        for tab in chrome.tabs {
            if tab_x + per > chrome.width {
                break;
            }
            if tab.active {
                quads.push(Quad::rounded(
                    tab_x,
                    4.0,
                    per - 4.0,
                    theme::TABBAR_HEIGHT - 8.0,
                    theme::TAB_ACTIVE_BG,
                    theme::TAB_RADIUS,
                ));
            }
            text.push(TextItem {
                text: tab.title.clone(),
                x: tab_x + theme::TAB_PADDING_X,
                y: 10.0,
                size: theme::BODY_SIZE,
                color: if tab.active { theme::TEXT_PRIMARY } else { theme::TEXT_SECONDARY },
                bold: false,
                italic: false,
                max_width: Some(per - theme::TAB_PADDING_X * 2.0),
            });
            tab_x += per;
        }
    }

    // Terminal viewport fills what is left.
    let terminal = Rect {
        x: content_x + theme::CONTENT_PADDING,
        y: theme::TABBAR_HEIGHT + theme::CONTENT_PADDING,
        w: (content_w - theme::CONTENT_PADDING * 2.0).max(1.0),
        h: (chrome.height - theme::TABBAR_HEIGHT - theme::CONTENT_PADDING * 2.0).max(1.0),
    };
    quads.push(Quad::solid(
        terminal.x,
        terminal.y,
        terminal.w,
        terminal.h,
        theme::TERMINAL_BG,
    ));

    Layout { quads, text, terminal }
}
