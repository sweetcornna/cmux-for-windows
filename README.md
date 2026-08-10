# cmux for Windows

A native Windows terminal workspace built with WinUI 3, ConPTY, Rust, and Ghostty's terminal engine.

> [!IMPORTANT]
> This repository is an independent, community-maintained Windows fork maintained by [sweetcornna](https://github.com/sweetcornna). It is not an official Manaflow project and is not endorsed, supported, or distributed by Manaflow. For the upstream macOS project, use [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux).

[Download the latest Windows installer](https://github.com/sweetcornna/cmux-for-windows/releases/latest)

## Highlights

- Native WinUI 3 desktop interface for Windows 10 version 2004 or newer.
- Real Windows pseudoterminals through ConPTY.
- Workspaces, screens, draggable split panes, and terminal and WebView2 browser tabs.
- Native keyboard, mouse, selection, clipboard, pane-focus, and pane-zoom interactions with uniformly sized compact action buttons and neutral pane borders.
- Stable live terminal and browser controls during topology polling and split-layout changes, with selected terminal canvases attached only after their hosts load so new tabs render immediately and reparented output does not stretch.
- Durable workspace topology across app restarts; restored terminals start fresh shells.
- Ghostty-compatible colors, fonts, palettes, bundled themes, and custom themes applied consistently to existing and newly created terminals, plus a live custom accent for shell actions and settings controls.
- English and Simplified Chinese interface strings.
- Optional Explorer integration in the Windows 11 first-level context menu, with a classic-menu fallback on Windows 10.
- A standalone `cmux-tui.exe` and local resource CLI for terminal automation.

## Project status

The Windows GUI and local multiplexer are usable, but this fork does not provide every feature of upstream cmux.

| Area | Status |
| --- | --- |
| Native WinUI 3 GUI | Supported |
| Local workspaces, screens, panes, tabs, and terminals | Supported |
| ConPTY shell sessions | Supported |
| Native WebView2 browser panes | Supported in the WinUI GUI; requires the Microsoft Edge WebView2 Runtime |
| Workspace topology restoration | Supported; processes and scrollback are not restored |
| Local control socket and resource CLI | Supported |
| Remote daemon, SSH, forwarding, enrollment, and relay | Not included |
| Cloud machine providers and machine agent | Not included |
| macOS and iOS applications | Not included |

Only 64-bit Windows builds using Rust's `x86_64-pc-windows-gnu` target are supported. There is no MSVC-ABI engine build.

## Install

1. Open the [latest release](https://github.com/sweetcornna/cmux-for-windows/releases/latest).
2. Download `cmux-windows-v<version>-setup.exe` and its matching `.sha256` file.
3. Verify the download in PowerShell:

   ```powershell
   $setup = ".\cmux-windows-v<version>-setup.exe"
   $expected = (Get-Content "$setup.sha256").Trim().ToLowerInvariant()
   $actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
   if ($actual -ne $expected) { throw "SHA-256 mismatch" }
   ```

4. Run the installer.

The installer is per-user, installs under `%LOCALAPPDATA%\Programs\cmux`, and does not require administrator privileges. Public Setup executables are currently unsigned, so Windows may show an unknown-publisher warning. Verify the SHA-256 value before continuing.

To register the Windows 11 first-level Explorer command without converting the application to MSIX, Setup installs a signed sparse identity package and trusts only its public signing certificate in the current user's `TrustedPeople` store. Setup removes an obsolete `cmux.Windows` full development MSIX during migration so Windows Search has only the installed shortcut, and that shortcut and the Explorer fallbacks reference the standalone upstream cmux icon explicitly. Uninstall removes the sparse package and that current-user certificate. Upgrades preserve settings and workspace data; uninstalling preserves that user data deliberately.

## Run the TUI

The standalone multiplexer and resource CLI use the same Windows engine:

```powershell
.\cmux-tui.exe
.\cmux-tui.exe --headless --session work
.\cmux-tui.exe --session work workspace create --name api
.\cmux-tui.exe --session work terminal list
```

The default shell is the first executable found from `pwsh.exe`, `powershell.exe`, and `cmd.exe`.

## Build from source

Clone with the Ghostty submodule:

```powershell
git clone --recurse-submodules https://github.com/sweetcornna/cmux-for-windows.git
Set-Location cmux-for-windows
```

Build the Rust engine and GUI:

```powershell
rustup target add x86_64-pc-windows-gnu
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu --locked
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

The complete toolchain and the GNU-host linker requirement are documented in [Development](docs/DEVELOPMENT.md).

## Data and diagnostics

| Purpose | Location |
| --- | --- |
| GUI settings | `%LOCALAPPDATA%\cmux\gui-settings.json` |
| Workspace state | `%LOCALAPPDATA%\cmux-tui\sessions` |
| TUI configuration | `%APPDATA%\cmux\cmux-tui.json` |
| GUI diagnostic log | `%LOCALAPPDATA%\cmux-gui.log` |
| Control sockets | `%TEMP%\cmux-tui-<user>` |

Terminal keystrokes and pasted text are intentionally never written to the diagnostic log.

## Documentation

- [Development and testing](docs/DEVELOPMENT.md)
- [Architecture and persistence](docs/ARCHITECTURE.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Release process](docs/RELEASING.md)
- [TUI and resource CLI](cmux-tui/README.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)
- [Upstream provenance](UPSTREAM.md)

## Relationship to upstream

This fork is derived from [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux) and retains the applicable copyright notices and license terms. The terminal parser and renderer are built from the repository's Ghostty submodule, which is derived from [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty).

The name “cmux” identifies the software lineage. It does not imply that Manaflow or the upstream maintainers publish or support these Windows builds. Bugs in this fork should be reported in this repository, not to the upstream project.

## License

The repository is distributed under the terms and scope described in [LICENSE](LICENSE). The Rust workspace manifests retain their inherited MIT declarations; this fork does not claim to relicense code beyond the rights established by upstream history and file-level notices. Third-party components retain their own terms; see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) and [Upstream provenance](UPSTREAM.md).
