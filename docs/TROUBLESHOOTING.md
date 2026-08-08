# Troubleshooting

## Windows warns about an unknown publisher

Public installers are currently unsigned. Download the matching `.sha256` sidecar from the same release and verify it before running Setup:

```powershell
$setup = ".\cmux-windows-v<version>-setup.exe"
$expected = (Get-Content "$setup.sha256").Trim().ToLowerInvariant()
$actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch" }
```

A matching hash verifies the downloaded bytes against the value published by this repository; it is not a substitute for Authenticode identity.

## The GUI opens without a working terminal

Confirm that `cmux_ffi.dll` is beside `CmuxGui.exe`. For a source build, build the debug FFI engine before the WinUI project:

```powershell
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu --locked
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

Review `%LOCALAPPDATA%\cmux-gui.log` for startup and topology errors. Do not post the log publicly without checking it for local paths or other sensitive context.

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

Run the installed application once after an upgrade. Setup invokes `CmuxGui.exe --repair-shell` and repairs the executable path only when Explorer integration had already been enabled. You can also disable and re-enable the integration from Settings.

## Browser panes do not work

Browser panes are experimental on Windows and are not part of the focused CI gate. Confirm a Chrome-family browser is installed. The default persistent profile is under `%LOCALAPPDATA%\cmux-tui\chrome-profile`; Chrome 136 and newer will not allow remote debugging against the normal OS-default profile.

## Where to report a bug

Use this repository's bug-report template and include the cmux version, Windows build, installation method, shell, exact reproduction steps, and relevant sanitized diagnostics. Do not report Windows-fork bugs to the upstream macOS project unless the issue has been independently confirmed in upstream code.
