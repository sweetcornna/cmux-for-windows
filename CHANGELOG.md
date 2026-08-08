# Changelog

All notable changes to the independently maintained Windows fork are documented here. This changelog does not describe releases of the upstream macOS project.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow the Windows package manifest.

## [Unreleased]

### Changed

- Reduced the repository to the native Windows GUI, Windows-compatible Rust engine and TUI, required Ghostty source, Windows CI, and supporting documentation.
- Rewrote project documentation to identify `sweetcornna` as the independent maintainer and to make the fork's unsupported relationship to Manaflow explicit.
- Added the same independent-fork disclosure, project link, and installed-notice guidance to the localized GUI About section.
- Included direct Ghostty and vendored Rust license texts in the Windows installer payload.

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
