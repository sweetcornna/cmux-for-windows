# Troubleshooting

## Windows warns about an unknown publisher

Public installers are currently unsigned. Download the matching `.sha256` sidecar from the same release and verify it before running Setup:

```powershell
$setup = ".\cmux-windows-v<version>-setup.exe"
$expected = (Get-Content "$setup.sha256").Trim().ToLowerInvariant()
$actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch" }
```

A matching hash verifies the downloaded bytes against the value published by this repository; it is not a substitute for Authenticode identity. The embedded sparse Explorer package is signed separately because Windows requires package identity for the Windows 11 menu; Setup trusts that exact public certificate only for the current user.

## The cmux commands appear only under “Show more options”

The first-level Windows 11 new-window and new-workspace commands require the sparse companion package in addition to the classic registry verbs. Confirm that Explorer integration is enabled in cmux Settings and that the package is registered:

```powershell
Get-AppxPackage -Name cmux.Windows.ShellIntegration
```

If it is missing, reinstall the current Setup build. If it is present, disable and re-enable Explorer integration, then open a fresh Explorer window. Windows 10 intentionally uses the classic menu only.

## The GUI opens without a working terminal

Confirm that `cmux_ffi.dll` is beside `CmuxGui.exe`. For a source build, build the debug FFI engine before the WinUI project:

```powershell
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu --locked
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

Review `%LOCALAPPDATA%\cmux-gui.log` for startup and topology errors. A newly selected terminal should progress from `TerminalView ctor` and `Loaded` to `CreateResources`, `SyncGrid`, and a nonzero `TabOpen`; if it stops after `Loaded`, rebuild or reinstall the current version. Do not post the log publicly without checking it for local paths or other sensitive context.

## Terminal glyphs, boxes, or cursors are misaligned

The Windows GUI uses DirectWrite/Direct2D and constrains every Ghostty grapheme to its assigned one- or two-cell grid span. Confirm the configured primary font is installed, then move the window once between displays if Windows recently changed display scaling. Rebuild both `cmux_ffi.dll` and the WinUI project together after a source update; an old DLL does not expose the presentation APIs used by the current GUI.

System DirectWrite fallback handles characters missing from the primary font, so CJK and emoji may use a different face while retaining the terminal grid. Exact glyph shapes can differ from Ghostty on another platform, but cell positions and cursor spans should remain aligned. If they do not, record the font name, Windows display scale, cursor shape, and whether the glyph occupies one or two cells; do not include terminal contents or credentials in diagnostics.

## No preferred shell starts

cmux searches `PATH` for `pwsh.exe`, then `powershell.exe`, then `cmd.exe`. If PowerShell 7 is installed but `cmd.exe` opens, start a new cmux process after confirming:

```powershell
Get-Command pwsh.exe
```

If none of the preferred executables can be resolved, the engine falls back to `cmd.exe`.

## Restored terminals are empty or start in the home directory

This is expected. cmux restores logical workspace topology, not live ConPTY processes. It does not restore previous process state, output, scrollback, command line, or working directory. Every restored terminal starts a fresh default shell.

## Build fails with `___chkstk_ms`

This commonly occurs on a `windows-gnu` Rust host when final linking goes through an external MinGW GCC. Force Rust's bundled linker components:

```powershell
cargo rustc --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu -- -C link-self-contained=y
cargo rustc --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui --bin cmux-tui --target x86_64-pc-windows-gnu -- -C link-self-contained=y
```

## bindgen reports `stddef.h` is missing

Ensure LLVM/Clang and MinGW-w64 are installed. If clang cannot discover MinGW headers, set `BINDGEN_EXTRA_CLANG_ARGS` with forward-slash paths. Backslashes are consumed as escapes by the parser.

See [Development](DEVELOPMENT.md#bindgen-cannot-find-standard-headers) for an example.

## Startup fails with access denied in the state directory

Set a temporary state root to isolate whether existing state or directory permissions are involved:

```powershell
$env:CMUX_TUI_STATE_DIR = "$env:TEMP\cmux-state-test"
```

If that works, preserve the original `%LOCALAPPDATA%\cmux-tui\sessions` directory before removing or repairing it. Workspace state is user data and is intentionally not deleted by uninstall.

## A workspace cannot be deleted

Windows does not allow deletion while a process still holds a file or directory handle. Close all cmux windows and `cmux-tui.exe` processes before retrying. Developers should drop registry and file objects before calling cleanup code rather than adding retry loops around sharing violations.

## Explorer integration points to an old installation

Setup re-registers the sparse package and invokes `CmuxGui.exe --repair-shell`, which preserves the enabled state and repairs the classic fallback's executable and standalone icon paths. You can also disable and re-enable the integration from Settings.

## Windows Search shows an old icon or two cmux applications

Older development builds could leave the full `cmux.Windows` MSIX installed beside the public per-user application. That package creates a separate Windows Search entry and can retain its old icon independently of the current executable. Reinstall the current Setup build; installation removes the obsolete full development package and recreates the Start menu shortcut with an explicit upstream cmux ICO. `Get-StartApps | Where-Object Name -Match cmux` should then return only the installed executable entry.

## Browser panes do not work

The native GUI uses the Microsoft Edge WebView2 Runtime, not Chrome remote debugging. If a browser tab reports an initialization error, confirm that the WebView2 Runtime is installed and repair or update it through the normal Microsoft Edge maintenance path. Browser page state and back/forward history are live process state; after restart, cmux restores the tab at its last durable URL.

The standalone TUI has a separate experimental Chrome DevTools Protocol transport and may use `%LOCALAPPDATA%\cmux-tui\chrome-profile`. That profile is not used by WinUI browser panes.

## Where to report a bug

Use this repository's bug-report template and include the cmux version, Windows build, installation method, shell, exact reproduction steps, and relevant sanitized diagnostics. Do not report Windows-fork bugs to the upstream macOS project unless the issue has been independently confirmed in upstream code.
