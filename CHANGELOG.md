# Changelog

All notable changes to the independently maintained Windows fork are documented here. This changelog does not describe releases of the upstream macOS project.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow the Windows package manifest.

## [Unreleased]

### Added

- Added local Claude Code, OpenCode, and Codex lifecycle integrations that display working, attention, idle, and completed state without reading terminal content.
- Added in-app completion and attention notifications that focus the exact workspace, screen, pane, and terminal tab by stable terminal identity without sending Windows system notifications.

### Fixed

- Restored each persistent terminal in its last validated local working directory using a new ConPTY process, with a safe home-directory fallback when the path is unavailable.
- Resumed hook-confirmed Claude Code, OpenCode, and Codex provider sessions after GUI restart while continuing to discard process state, output, scrollback, and arbitrary command lines.
- Aligned the resource CLI's agent state vocabulary with the Rust core's `working`, `blocked`, `idle`, `done`, and `unknown` protocol values.

## [0.3.6] - 2026-08-12

### Fixed

- Added terminal `Ctrl+V` paste alongside the existing paste shortcuts.
- Routed committed Windows input-method text through the native text services so Chinese and other non-English input methods work in terminal panes.

## [0.3.5] - 2026-08-12

### Added

- Added a centralized Windows shortcut catalog covering workspace, screen, pane, tab, browser, and terminal actions, including position selection for the first ten workspaces, screens, and tabs.
- Added a package-free shortcut regression executable and Windows CI coverage for every defined action, exact modifier/context matching, AltGr exclusion, and destructive-key repeat protection.

### Fixed

- Routed application shortcuts before terminal and WebView input, while preserving text input in address/search fields, Settings, modal dialogs, and Right Alt/AltGr layouts.
- Completed terminal physical-key forwarding for OEM and international punctuation, left/right modifiers, Windows/Super, lock state, system keys, function/navigation keys, and numpad variants, including press, repeat, release, and focus-reset behavior.
- Cleared stale native character suppression when the active workspace input target changes, preventing delayed character messages from being dropped.

## [0.3.4] - 2026-08-11

### Changed

- Replaced run-shaped terminal text with native DirectWrite/Direct2D per-grapheme grid rendering, preserving Ghostty's one- and two-cell placement across fallback fonts, CJK, emoji, box drawing, bold, and italic text.
- Published physical cell metrics to the Rust mux so ConPTY pixel geometry follows the WinUI renderer at the current display DPI.

### Fixed

- Kept block-cursor glyphs readable, honored steady and blinking cursor modes and blinking text, applied complete inverse colors, and rendered selection foreground/background colors from the active mux theme.

## [0.3.3] - 2026-08-11

### Fixed

- Kept workspace views mounted across navigation and restored terminal polling after internal reparenting so Explorer launches, workspace and terminal-tab switches, and topology refreshes accept input without a reload gap or extra click.
- Hid the mounted workspace surface while Settings is open so terminal output does not show through the transparent settings page.
- Preselected split panes from the foreground left-button signal, observed handled terminal-body pointer presses at the pane boundary, and kept pane-focus-only snapshot changes out of topology reconstruction so immediate input reaches the clicked pane without a delayed tab rebuild reclaiming focus.

## [0.3.2] - 2026-08-10

### Fixed

- Reserved explicit settings-card action widths so color controls, image actions, switches, and selectors remain fully visible when the window is maximized.

## [0.3.1] - 2026-08-09

### Fixed

- Kept settings controls within the visible page width when resizing or maximizing the window.

## [0.3.0] - 2026-08-09

### Added

- Added optional first-level Windows 11 Explorer commands for opening a folder in a new cmux window or new cmux workspace, backed by a native `IExplorerCommand` server and signed sparse identity package, while retaining classic folder verbs for Windows 10.

### Changed

- Reduced the repository to the native Windows GUI, Windows-compatible Rust engine and TUI, required Ghostty source, Windows CI, and supporting documentation.
- Rewrote project documentation to identify `sweetcornna` as the independent maintainer and to make the fork's unsupported relationship to Manaflow explicit.
- Added the same independent-fork disclosure, project link, and installed-notice guidance to the localized GUI About section.
- Included direct Ghostty and vendored Rust license texts in the Windows installer payload.
- Replaced the placeholder Windows icons and title-bar glyph with authoritative upstream cmux artwork across every Win32 ICO frame, executable, installer shortcut, package, taskbar, and Explorer command; Setup now removes the obsolete full development MSIX that could leave a duplicate stale Windows Search entry.
- Reconciled volatile snapshot updates without rebuilding unchanged pane controls, corrected action-button icon clipping, and moved split dragging to an accessible native WinUI thumb.
- Standardized screen and pane action buttons at 30 DIP and replaced the active pane's blue outline with the neutral pane border while retaining logical focus behavior.
- Applied Ghostty themes and custom terminal foreground/background colors through mux-wide defaults so visible, hidden, restored, and newly created terminals update consistently, including immediate restoration of the Ghostty baseline when following the config again; custom shell accents now update bound actions and settings controls through one shared live brush.
- Resynchronized and repainted terminal canvases after split, zoom, unzoom, close, and other host reparenting operations to prevent stale backing surfaces from stretching terminal output.
- Deferred Win2D canvas attachment until a terminal host finishes loading, preventing newly created terminal tabs from remaining blank when WinUI delivers an early unmatched unload event.

## [0.2.1] - 2026-08-08

### Added

- Per-user Inno Setup installer with Start menu integration, optional desktop shortcut, uninstall registration, and SHA-256 sidecar.
- Installer upgrade repair for previously enabled Explorer context-menu integration.

### Changed

- Published the WinUI application and Rust engine as a self-contained Windows installation.
- Preserved user settings and durable workspace state during uninstall.

## [0.2.0] - 2026-08-08

### Added

- Native WinUI 3 frontend backed by the Rust multiplexer through `cmux_ffi.dll`.
- ConPTY terminal sessions with keyboard and mouse input.
- Workspaces, splits, pane tabs, focus handling, and durable workspace topology.
- Ghostty-compatible colors, fonts, palettes, bundled themes, background images, and appearance settings.
- English and Simplified Chinese localization.
- Optional Explorer context-menu integration.
- Development MSIX packaging support.

### Security

- Prevented terminal key and character content from being written to diagnostics.

## [0.1.0] - 2026-08-06

### Added

- Native Windows support for the Rust TUI and local resource CLI using the `x86_64-pc-windows-gnu` target.
- ConPTY process hosting, Windows AF_UNIX control sockets, Windows state/config paths, and platform-specific shell selection.
