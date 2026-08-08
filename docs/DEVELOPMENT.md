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
- Inno Setup 6 or 7 only when building the public installer.
- Visual Studio Build Tools only when building the optional development MSIX.

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
  -p cmux-tui-core --lib `
  --target x86_64-pc-windows-gnu `
  --locked `
  workspace_registry::tests::

cargo test --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui-core --lib `
  --target x86_64-pc-windows-gnu `
  --locked `
  persistent_gui_restart_restores_workspaces_with_fresh_default_shells

dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

The full inherited Rust test suite is not a supported Windows gate. Some retained core test modules still hard-code `/bin/sh` and `/tmp`, and ConPTY does not yet have complete automated coverage.

For GUI changes, manually verify:

1. Create, rename, switch, and close workspaces.
2. Create splits and tabs, then change focus with mouse and keyboard.
3. Type and paste into PowerShell and `cmd.exe` terminals.
4. Restart the application and confirm topology restoration with fresh shells.
5. Change appearance, theme, font, and language settings.
6. Enable and disable Explorer integration when that code changes.
7. Confirm `%LOCALAPPDATA%\cmux-gui.log` contains no input content.

## Repository layout

| Path | Purpose |
| --- | --- |
| `windows/CmuxGui` | Native WinUI 3 frontend |
| `windows/installer` | Inno Setup definition |
| `windows/scripts` | Installer and development MSIX scripts |
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
