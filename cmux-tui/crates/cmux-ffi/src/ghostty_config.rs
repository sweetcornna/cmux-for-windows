//! Reader for Ghostty's own configuration and theme files.
//!
//! cmux embeds Ghostty's VT engine, so honoring Ghostty's config is what makes
//! a cmux terminal actually look like the user's Ghostty. The format is plain
//! `key = value` lines with `#` comments, which is stable enough to read
//! directly rather than linking Ghostty's Zig config parser.
//!
//! Only the presentation subset is read: colors, palette, and font. Everything
//! else in a Ghostty config is ignored rather than guessed at.

use std::path::{Path, PathBuf};

use ghostty_vt::Rgb;

/// Ghostty's built-in defaults, from `src/config/Config.zig`.
pub const DEFAULT_BACKGROUND: Rgb = Rgb { r: 0x28, g: 0x2C, b: 0x34 };
pub const DEFAULT_FOREGROUND: Rgb = Rgb { r: 0xFF, g: 0xFF, b: 0xFF };

#[derive(Debug, Clone)]
pub struct GhosttyConfig {
    pub background: Option<Rgb>,
    pub foreground: Option<Rgb>,
    pub cursor_color: Option<Rgb>,
    pub selection_background: Option<Rgb>,
    pub selection_foreground: Option<Rgb>,
    pub palette: [Option<Rgb>; 256],
    pub font_family: Option<String>,
    pub font_size: Option<f32>,
    /// Path the settings came from, or `None` when no config file exists.
    pub source: Option<PathBuf>,
}

// `Default` is only derivable for arrays up to 32 elements, and the palette
// has 256.
impl Default for GhosttyConfig {
    fn default() -> Self {
        Self {
            background: None,
            foreground: None,
            cursor_color: None,
            selection_background: None,
            selection_foreground: None,
            palette: [None; 256],
            font_family: None,
            font_size: None,
            source: None,
        }
    }
}

impl GhosttyConfig {
    /// Load the user's Ghostty config, following `theme` if present.
    ///
    /// Returns defaults when no config exists, so callers always get a usable
    /// appearance rather than having to special-case a missing file.
    pub fn load() -> Self {
        let Some(dir) = config_dir() else {
            return Self::default();
        };
        let path = dir.join("config");
        let Ok(text) = std::fs::read_to_string(&path) else {
            return Self::default();
        };

        let mut config = Self::default();

        // A theme is a config fragment applied first, so explicit keys in the
        // user's own config win over whatever the theme set.
        if let Some(theme) = find_value(&text, "theme")
            && let Some(theme_text) = read_theme(&dir, &theme)
        {
            config.apply(&theme_text);
        }

        config.apply(&text);
        config.source = Some(path);
        config
    }

    /// Merge a config fragment over the current values.
    ///
    /// Later keys win, which is how a theme is layered under a user's own
    /// config and how a runtime theme switch is layered over it.
    pub fn apply(&mut self, text: &str) {
        for (key, value) in entries(text) {
            match key {
                "background" => self.background = parse_color(value),
                "foreground" => self.foreground = parse_color(value),
                "cursor-color" => self.cursor_color = parse_color(value),
                "selection-background" => self.selection_background = parse_color(value),
                "selection-foreground" => self.selection_foreground = parse_color(value),
                "font-size" => self.font_size = value.parse::<f32>().ok().filter(|s| *s > 0.0),
                // Ghostty allows repeats to build a fallback chain; the first
                // is the primary face and the only one a cell grid needs.
                "font-family" => {
                    if self.font_family.is_none() {
                        let name = value.trim_matches('"').trim();
                        if !name.is_empty() {
                            self.font_family = Some(name.to_string());
                        }
                    }
                }
                // `palette = N=#RRGGBB`
                "palette" => {
                    if let Some((index, color)) = value.split_once('=')
                        && let Ok(index) = index.trim().parse::<usize>()
                        && index < 256
                        && let Some(rgb) = parse_color(color.trim())
                    {
                        self.palette[index] = Some(rgb);
                    }
                }
                _ => {}
            }
        }
    }
}

/// Ghostty's config directory.
///
/// Ghostty documents `$XDG_CONFIG_HOME/ghostty` or `~/.config/ghostty` and has
/// no Windows layout of its own, since it does not run there. `%APPDATA%` is
/// the Windows convention and matches where cmux keeps its own config, with
/// XDG still honored first for people who set it.
fn config_dir() -> Option<PathBuf> {
    if let Some(xdg) = non_empty_var("XDG_CONFIG_HOME") {
        return Some(PathBuf::from(xdg).join("ghostty"));
    }
    #[cfg(windows)]
    {
        if let Some(appdata) = non_empty_var("APPDATA") {
            return Some(PathBuf::from(appdata).join("ghostty"));
        }
    }
    let home = non_empty_var("HOME").or_else(|| non_empty_var("USERPROFILE"))?;
    Some(PathBuf::from(home).join(".config").join("ghostty"))
}

fn non_empty_var(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.trim().is_empty())
}

/// Read a theme by name from the config directory's `themes` folder.
///
/// Ghostty also searches its installed resources directory, which does not
/// exist on Windows, so only the user directory is consulted here.
fn read_theme(dir: &Path, name: &str) -> Option<String> {
    let name = name.trim().trim_matches('"');
    if name.is_empty() || name.contains(['/', '\\']) || name.contains("..") {
        // Theme names index a directory; refuse anything path-like.
        return None;
    }
    std::fs::read_to_string(dir.join("themes").join(name)).ok()
}

fn find_value(text: &str, wanted: &str) -> Option<String> {
    entries(text)
        .find(|(key, _)| *key == wanted)
        .map(|(_, value)| value.to_string())
}

/// Yield `key = value` pairs, skipping comments and blank lines.
fn entries(text: &str) -> impl Iterator<Item = (&str, &str)> {
    text.lines().filter_map(|line| {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            return None;
        }
        let (key, value) = line.split_once('=')?;
        Some((key.trim(), value.trim()))
    })
}

/// Parse `#RRGGBB`, `RRGGBB`, or `#RGB`.
///
/// Ghostty also accepts X11 color names; those are rare in themes and are
/// deliberately not guessed at here.
pub fn parse_color(value: &str) -> Option<Rgb> {
    let hex = value.trim().trim_start_matches('#');
    match hex.len() {
        6 => {
            let r = u8::from_str_radix(&hex[0..2], 16).ok()?;
            let g = u8::from_str_radix(&hex[2..4], 16).ok()?;
            let b = u8::from_str_radix(&hex[4..6], 16).ok()?;
            Some(Rgb { r, g, b })
        }
        3 => {
            let expand = |c: &str| u8::from_str_radix(c, 16).ok().map(|v| v * 17);
            Some(Rgb {
                r: expand(&hex[0..1])?,
                g: expand(&hex[1..2])?,
                b: expand(&hex[2..3])?,
            })
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_hex_colors_in_both_lengths() {
        assert_eq!(parse_color("#1e1e2e"), Some(Rgb { r: 0x1e, g: 0x1e, b: 0x2e }));
        assert_eq!(parse_color("1e1e2e"), Some(Rgb { r: 0x1e, g: 0x1e, b: 0x2e }));
        assert_eq!(parse_color("#abc"), Some(Rgb { r: 0xaa, g: 0xbb, b: 0xcc }));
        assert_eq!(parse_color("not-a-color"), None);
    }

    #[test]
    fn reads_colors_font_and_indexed_palette() {
        let mut config = GhosttyConfig::default();
        config.apply(
            "# a comment\n\
             background = #1e1e2e\n\
             foreground = cdd6f4\n\
             font-family = JetBrains Mono\n\
             font-size = 13.5\n\
             palette = 1=#f38ba8\n\
             palette = 15=#a6adc8\n",
        );

        assert_eq!(config.background, Some(Rgb { r: 0x1e, g: 0x1e, b: 0x2e }));
        assert_eq!(config.foreground, Some(Rgb { r: 0xcd, g: 0xd6, b: 0xf4 }));
        assert_eq!(config.font_family.as_deref(), Some("JetBrains Mono"));
        assert_eq!(config.font_size, Some(13.5));
        assert_eq!(config.palette[1], Some(Rgb { r: 0xf3, g: 0x8b, b: 0xa8 }));
        assert_eq!(config.palette[15], Some(Rgb { r: 0xa6, g: 0xad, b: 0xc8 }));
        assert_eq!(config.palette[2], None);
    }

    #[test]
    fn later_entries_override_earlier_ones_so_config_beats_theme() {
        let mut config = GhosttyConfig::default();
        config.apply("background = #000000\n");
        config.apply("background = #ffffff\n");
        assert_eq!(config.background, Some(Rgb { r: 0xff, g: 0xff, b: 0xff }));
    }

    #[test]
    fn first_font_family_wins_because_repeats_are_a_fallback_chain() {
        let mut config = GhosttyConfig::default();
        config.apply("font-family = Cascadia Mono\nfont-family = Noto Color Emoji\n");
        assert_eq!(config.font_family.as_deref(), Some("Cascadia Mono"));
    }

    #[test]
    fn theme_names_may_not_escape_the_themes_directory() {
        let dir = Path::new("/nonexistent");
        assert!(read_theme(dir, "../../etc/passwd").is_none());
        assert!(read_theme(dir, "sub/dir").is_none());
    }
}
