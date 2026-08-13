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
- DirectWrite/Direct2D terminal rendering that shapes each Ghostty grapheme inside its assigned one- or two-cell grid span, including readable block cursors, selection colors, text blink, and color-font fallback.
- Stable live terminal and browser controls during pane and workspace switches, topology polling, and split-layout changes, with inactive terminal rendering paused and selected terminals ready for input without a visual-tree reload gap.
- Durable workspace topology and per-terminal working directories across app restarts; terminals use new ConPTY processes, while active Claude Code, OpenCode, and Codex sessions resume through their provider CLIs.
- Ghostty-compatible colors, fonts, palettes, bundled themes, and custom themes applied consistently to existing and newly created terminals, plus a live custom accent for shell actions and settings controls.
- A responsive settings page that keeps controls within the visible width and hides the mounted terminal surface while settings are open.
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
| Workspace topology restoration | Supported with per-terminal local working directories; processes, output, and scrollback are not restored |
| Claude Code, OpenCode, and Codex sessions | Status, in-app completion/attention notifications, and provider-session resume are supported for sessions launched inside the Windows GUI; no Windows system notifications are sent |
| Local control socket and resource CLI | Supported |
| Remote daemon, SSH, forwarding, enrollment, and relay | Not included |
| Cloud machine providers and machine agent | Not included |
| macOS and iOS applications | Not included |

Only 64-bit Windows builds using Rust's `x86_64-pc-windows-gnu` target are supported. There is no MSVC-ABI engine build.

## Keyboard shortcuts

| Scope | Shortcut | Action |
| --- | --- | --- |
| App | `Ctrl+Shift+K` / `Ctrl+,` | Focus workspace search / open Settings |
| Workspace | `Ctrl+Shift+N` | Create a workspace |
| Workspace | `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown` | Select the previous / next workspace |
| Workspace | `Ctrl+Shift+1` … `Ctrl+Shift+9`, `Ctrl+Shift+0` | Select workspace 1 … 10 |
| Workspace | `Ctrl+Alt+Shift+Up` / `Ctrl+Alt+Shift+Down` | Move the workspace up / down |
| Workspace | `Ctrl+Alt+Shift+F2` / `Ctrl+Alt+Shift+W` | Rename / close the workspace |
| Screen | `Ctrl+Alt+N` | Create a screen |
| Screen | `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown` | Select the previous / next screen |
| Screen | `Ctrl+Alt+1` … `Ctrl+Alt+9`, `Ctrl+Alt+0` | Select screen 1 … 10 |
| Screen | `Ctrl+Alt+F2` / `Ctrl+Alt+W` | Rename / close the screen |
| Pane | `Ctrl+Shift+\` / `Ctrl+Shift+-` | Split right / down |
| Pane | `Ctrl+Alt+Arrow` | Focus the pane in the arrow direction |
| Pane | `Ctrl+Shift+Enter` | Zoom or restore the active pane |
| Pane | `Ctrl+Shift+F2` / `Ctrl+Shift+W` | Rename / close the pane |
| Tab | `Ctrl+T` / `Ctrl+Shift+T` | Create a terminal / browser tab |
| Tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` | Select the next / previous tab |
| Tab | `Ctrl+1` … `Ctrl+9`, `Ctrl+0` | Select tab 1 … 10 |
| Tab | `Ctrl+Alt+Shift+Left` / `Ctrl+Alt+Shift+Right` | Move the tab left / right |
| Tab | `Ctrl+Alt+Shift+1` … `Ctrl+Alt+Shift+9`, `Ctrl+Alt+Shift+0` | Move the tab to pane 1 … 10 |
| Tab | `Ctrl+F2` / `Ctrl+W` | Rename / close the tab |
| Browser | `Alt+Left` / `Alt+Right` | Go back / forward |
| Browser | `Ctrl+R` or `F5` / `Ctrl+L` | Reload / focus the address bar |
| Terminal | `Ctrl+Shift+C` or `Ctrl+Insert` | Copy the selection |
| Terminal | `Ctrl+V`, `Ctrl+Shift+V`, or `Shift+Insert` | Paste |
| Terminal | `Ctrl+Shift+A` | Select all |

Terminal applications continue to receive `Ctrl`, `Alt`, `Shift`, Windows/Super, function, navigation, numpad, system, international, and punctuation keys that do not exactly match an application shortcut. Right Alt/AltGr and Windows input methods remain available for Unicode text input. Application shortcuts work from terminal and WebView content, while text fields, Settings, and modal dialogs retain their own input.

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

The default shell is the first executable found from `pwsh.exe`, `powershell.exe`, and `cmd.exe`. In the Windows GUI, Claude Code, OpenCode, and Codex launched from terminal panes receive per-invocation local status hooks. cmux does not modify the providers' global user configuration and does not collect prompts, assistant responses, or terminal contents.

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

This fork is derived from [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux) and retains the applicable copyright notices and license terms. Terminal parsing and state come from the repository's Ghostty submodule, which is derived from [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty); the Windows GUI renders that state with DirectWrite/Direct2D.

The name “cmux” identifies the software lineage. It does not imply that Manaflow or the upstream maintainers publish or support these Windows builds. Bugs in this fork should be reported in this repository, not to the upstream project.

## License

The repository is distributed under the terms and scope described in [LICENSE](LICENSE). The Rust workspace manifests retain their inherited MIT declarations; this fork does not claim to relicense code beyond the rights established by upstream history and file-level notices. Third-party components retain their own terms; see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) and [Upstream provenance](UPSTREAM.md).
