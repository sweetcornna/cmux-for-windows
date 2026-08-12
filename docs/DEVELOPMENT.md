# Development

This guide describes the supported Windows development environment for the native GUI, Rust engine, and standalone TUI.

## Supported environment

- 64-bit Windows 10 version 2004 (build 19041) or newer.
- Rust 1.91 or newer with the `x86_64-pc-windows-gnu` target.
- Zig 0.16.0, matching the version required by the Ghostty submodule.
- MinGW-w64 GCC using the MSVCRT environment.
- LLVM/Clang with `libclang` available to bindgen.
- .NET 8 SDK.
- Git with submodule support.
- Visual Studio Build Tools with the x64 C++ toolset and Windows 10/11 SDK when building Explorer integration, the installer, or the development MSIX.
- Microsoft Edge WebView2 Runtime when exercising native browser panes.
- Inno Setup 6 or 7 only when building the public installer.

The Rust engine has a GNU ABI even when the host Rust toolchain uses MSVC. Do not change the target to `x86_64-pc-windows-msvc`: Ghostty's Zig-built static library and the Rust artifact would use incompatible ABIs.

## Clone

```powershell
git clone --recurse-submodules https://github.com/sweetcornna/cmux-for-windows.git
Set-Location cmux-for-windows
git submodule update --init --depth 1 ghostty
```

The only required submodule is `ghostty`.

## Install the Rust target

```powershell
rustup target add x86_64-pc-windows-gnu
```

A standard MSVC-host Rust installation can cross-build the GNU target. A GNU-host installation avoids Visual Studio for Rust compilation but needs the linker workaround described below.

Make sure MinGW and libclang are discoverable. A typical GitHub Actions/MSYS2 path is:

```powershell
$env:Path = "C:\msys64\mingw64\bin;$env:Path"
```

## Build the engine

From the repository root:

```powershell
cargo build --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  --locked
```

`ghostty-vt-sys` invokes Zig and bindgen automatically. The build produces:

- `cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe`
- `cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux_ffi.dll`

### GNU-host linker failure

On a `windows-gnu` Rust host, rustc can delegate final linking to an external MinGW GCC and fail with `___chkstk_ms` relocation errors. Force Rust's bundled linker components for each final artifact:

```powershell
cargo rustc --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  -- -C link-self-contained=y

cargo rustc --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui --bin cmux-tui `
  --target x86_64-pc-windows-gnu `
  -- -C link-self-contained=y
```

The MSVC-host CI runner cross-builds the GNU target and does not require these explicit flags.

### bindgen cannot find standard headers

`BINDGEN_EXTRA_CLANG_ARGS` is parsed with shell quoting rules. Use forward slashes even in Windows paths:

```powershell
$env:BINDGEN_EXTRA_CLANG_ARGS = @(
  "--target=x86_64-w64-mingw32"
  "-isystem C:/msys64/mingw64/lib/gcc/x86_64-w64-mingw32/<version>/include"
  "-isystem C:/msys64/mingw64/x86_64-w64-mingw32/include"
) -join " "
```

A corrupted or incomplete header path usually appears as `fatal error: 'stddef.h' file not found`.

## Build and run the GUI

Build the debug `cmux_ffi.dll` first, then build the unpackaged WinUI application:

```powershell
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

Run the generated `CmuxGui.exe` from the matching `bin\Debug\net8.0-windows10.0.19041.0\win-x64` directory. The project copies the debug engine next to the application when it exists.

The GUI is self-contained with respect to Windows App SDK. It does not depend on the machine's installed Windows App Runtime version.

### Build the Windows 11 Explorer command

The first-level Windows 11 context menu uses the native x64 `IExplorerCommand` server under `windows\CmuxShellExtension`. From a Visual Studio Developer PowerShell:

```powershell
msbuild .\windows\CmuxShellExtension\CmuxShellExtension.vcxproj `
  /p:Configuration=Debug /p:Platform=x64
```

`windows\scripts\package.ps1` and `windows\scripts\installer.ps1` build this project automatically. An ordinary unpackaged GUI build can still register the classic Explorer verbs, but the first-level Windows 11 command requires either the development MSIX or the signed sparse package produced for the Inno installer.

## Run the TUI and local CLI

```powershell
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe --headless --session dev
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe --session dev workspace create --name test
```

The default session is `main`. The GUI uses the `cmux-gui` session.

## Test

Run formatting, the Windows binaries, focused core tests, and the WinUI build:

```powershell
cargo fmt --manifest-path .\cmux-tui\Cargo.toml --all -- --check

cargo build --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  --locked

cargo test --manifest-path .\cmux-tui\Cargo.toml `
  -p ghostty-vt --lib `
  --target x86_64-pc-windows-gnu `
  --locked `
  key::tests::chord_parser_encodes_common_keys

cargo test --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui-core --lib `
  --target x86_64-pc-windows-gnu `
  --locked `
  workspace_registry::tests::

cargo test --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui-core --lib `
  --target x86_64-pc-windows-gnu `
  --locked `
  persistent_gui_restart_restores_workspaces_with_fresh_default_shells

dotnet run --project .\windows\CmuxGui.ShortcutTests\CmuxGui.ShortcutTests.csproj -c Release

dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

The full inherited Rust test suite is not a supported Windows gate. Some retained core test modules still hard-code `/bin/sh` and `/tmp`, and ConPTY does not yet have complete automated coverage.

For GUI changes, manually verify:

1. Create, rename, reorder, switch, and close workspaces. Exercise `Ctrl+Shift+N`, `Ctrl+Shift+PageUp/PageDown`, `Ctrl+Shift+1` through `Ctrl+Shift+0`, `Ctrl+Alt+Shift+Up/Down`, `Ctrl+Alt+Shift+F2`, and `Ctrl+Alt+Shift+W`; confirm the selected workspace terminal accepts input immediately without an extra click.
2. Create, rename, switch, and close screens with `Ctrl+Alt+N`, `Ctrl+Alt+PageUp/PageDown`, `Ctrl+Alt+1` through `Ctrl+Alt+0`, `Ctrl+Alt+F2`, and `Ctrl+Alt+W`; confirm each screen exposes its own pane topology.
3. Split panes with `Ctrl+Shift+\\` and `Ctrl+Shift+-`, drag split and viewport dividers, repeatedly click each terminal body and confirm immediate input reaches only that pane, move focus by mouse and `Ctrl+Alt+Arrow`, toggle zoom with `Ctrl+Shift+Enter`, rename with `Ctrl+Shift+F2`, and close with `Ctrl+Shift+W`. Confirm these application shortcuts work immediately after pane, tab, and workspace switches and are not sent to the shell. Hold each destructive shortcut and confirm it executes only once. Repeat split → zoom → unzoom → close cycles and confirm terminal glyphs retain their normal aspect ratio and repaint at the final pane size.
4. Run a complex TUI such as Claude Code and verify box drawing, inverse-video controls, bold/italic text, CJK, emoji, combining characters, and one- and two-cell glyphs stay on the terminal grid. Exercise block, bar, underline, hollow, steady, and blinking cursors; a block cursor over text must keep the glyph readable. Move the window between displays at 100%, 150%, and 200% scaling and confirm glyphs, pointer hit testing, and ConPTY rows/columns remain aligned.
5. Create, rename, reorder, move between panes, focus, and close terminal and browser tabs. Exercise `Ctrl+T`, `Ctrl+Shift+T`, `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+1` through `Ctrl+0`, `Ctrl+Alt+Shift+Left/Right`, `Ctrl+Alt+Shift+1` through `Ctrl+Alt+Shift+0`, `Ctrl+F2`, and `Ctrl+W`. Switch terminal tabs repeatedly, wait across a topology-polling interval, and confirm the selected terminal still accepts keyboard input without an extra click.
6. Exercise press, repeat, and release handling on the main keyboard and numpad; verify terminal input using left and right `Ctrl`, `Alt`, `Shift`, and Windows/Super, CapsLock/NumLock state, function and navigation keys, PrintScreen, ScrollLock, Pause, the menu key, the international backslash key, and OEM punctuation such as `Ctrl+[`, `Ctrl+]`, `Ctrl+/`, and `Ctrl+\\`. Verify Right Alt/AltGr still produces layout text instead of being encoded as `Ctrl+Alt`; test `Ctrl+Shift+C`, `Ctrl+Insert`, `Ctrl+Shift+V`, `Shift+Insert`, and `Ctrl+Shift+A`; type into PowerShell and `cmd.exe`, verify selection colors and soft wraps, paste with and without bracketed-paste mode, and confirm Shift forces local selection when terminal mouse reporting is enabled.
7. In browser content and the address bar, exercise `Alt+Left/Right`, `Ctrl+R`, `F5`, `Ctrl+L`, and the workspace/screen/pane/tab shortcuts. Navigate through a redirect and verify the document title reaches the tab. Confirm typing and editing remain local in the address bar, workspace search, Settings, and every modal dialog, and that an unavailable WebView2 Runtime produces a visible browser error rather than closing the workspace.
8. Restart the main application and confirm workspace, screen, pane, tab, layout, focus, names, and browser URLs are restored with fresh shells and fresh WebView controls; terminal processes, output, scrollback, commands, working directories, page state, and browser history must not be restored.
9. Launch a second new-workspace activation and confirm it is forwarded to the existing main window. Launch a new-window activation and confirm it uses an independent transient mux whose topology is not restored later.
10. Change appearance, theme, font, language, background, opacity, and Explorer-integration settings. Confirm the settings surface does not reveal terminal output underneath it. Confirm terminal foreground/background overrides and full theme palettes update existing visible and hidden terminals, then create another terminal and confirm it inherits the same colors. Select Follow config or reset the overrides and confirm existing terminals immediately return to the Ghostty configuration baseline; also confirm the accent picker immediately updates the new-workspace action, settings sliders, and project link.
11. Leave terminal and browser panes idle across multiple topology-polling intervals and confirm the controls do not flash or reconstruct; confirm every screen and pane action is uniformly 30 × 30 DIP with a complete centered icon at normal and narrow window sizes, and that changing pane focus does not add an accent-colored outer border.
12. Enable Explorer integration and confirm both “Open in new cmux window” and “Open in new cmux workspace” appear directly in the Windows 11 menu for a filesystem folder. Launch each command and confirm its selected terminal accepts keyboard input immediately without an extra click; disable integration and confirm both commands disappear. On Windows 10, verify the classic fallbacks instead.
13. Inspect only diagnostics produced during the acceptance run and confirm they contain no terminal key events, character events, pasted text, credentials, or terminal screen content.

## Repository layout

| Path | Purpose |
| --- | --- |
| `windows/CmuxGui` | Native WinUI 3 frontend |
| `windows/CmuxShellExtension` | Native x64 `IExplorerCommand` COM server |
| `windows/CmuxShellPackage` | Sparse package manifest for the Inno installation |
| `windows/installer` | Inno Setup definition |
| `windows/scripts` | Installer, sparse package, and development MSIX scripts |
| `cmux-tui/crates/cmux-ffi` | C ABI consumed by WinUI |
| `cmux-tui/crates/cmux-tui-core` | Authoritative multiplexer and persistence model |
| `cmux-tui/crates/cmux-pty` | PTY abstraction and ConPTY backend |
| `cmux-tui/crates/ghostty-vt*` | Rust bindings around Ghostty's terminal engine |
| `cmux-tui/crates/cmux-tui` | Standalone TUI and local resource CLI |
| `ghostty` | Terminal-engine source submodule |

## Windows portability rules

- Use `platform::sync_directory()` instead of opening a directory as a file.
- Release open handles before deleting or replacing files and directories.
- Put platform path, shell, permission, and synchronization decisions in `cmux-tui-core::platform`.
- Gate tests that reference Unix-only items at the test itself.
- Keep the core registry authoritative; frontends reconcile from core snapshots after mutations.
- Treat all terminal input and output as sensitive. Do not add content logging to diagnostics.
