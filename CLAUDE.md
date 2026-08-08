# cmux for Windows contributor notes

## Repository scope

This is `sweetcornna`'s independently maintained Windows fork. It is not an official Manaflow repository. Keep changes focused on:

- `windows/`: native WinUI 3 GUI, packaging, and installer;
- `cmux-tui/`: Windows-compatible Rust engine, TUI, and local resource CLI;
- `ghostty/`: required terminal-engine submodule;
- `scripts/`: the Ghostty Zig version helper used by Windows CI;
- `.github/` and `docs/`: Windows project automation and documentation.

Do not reintroduce the upstream macOS, iOS, web, cloud, Homebrew, Unix remote-daemon, relay, or SDK publishing trees.

## Supported target

The engine target is `x86_64-pc-windows-gnu`. There is no supported MSVC-ABI engine build.

```powershell
rustup target add x86_64-pc-windows-gnu
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui -p cmux-ffi --target x86_64-pc-windows-gnu --locked
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

On a `windows-gnu` Rust host, final linking may require:

```powershell
cargo rustc --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu -- -C link-self-contained=y
cargo rustc --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui --bin cmux-tui --target x86_64-pc-windows-gnu -- -C link-self-contained=y
```

## Required checks

```powershell
cargo fmt --manifest-path .\cmux-tui\Cargo.toml --all -- --check
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui -p cmux-ffi --target x86_64-pc-windows-gnu --locked
cargo test --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui-core --lib --target x86_64-pc-windows-gnu --locked workspace_registry::tests::
cargo test --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui-core --lib --target x86_64-pc-windows-gnu --locked persistent_gui_restart_restores_workspaces_with_fresh_default_shells
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

The complete Rust suite is not portable to Windows because some retained core tests still assume `/bin/sh` and `/tmp`.

## Windows invariants

- Never use `File::open` on a directory. Use `cmux-tui-core::platform::sync_directory()`.
- Drop registries, files, and native handles before deleting their paths; Windows rejects deletion while a handle is open.
- Keep platform choices in `cmux-tui-core::platform`, not scattered conditional blocks.
- A test that references a Unix-only item must carry its own `cfg(unix)` gate.
- Zig target selection must not assume `host == target` means the native ABI is correct. A Windows GNU Rust target requires a Windows GNU Zig target.
- `BINDGEN_EXTRA_CLANG_ARGS` paths must use forward slashes because bindgen parses the variable with shell quoting rules.
- The Rust core owns workspace topology. The WinUI layer sends mutations by stable public IDs and must not persist a competing topology.
- Closing the GUI preserves topology but does not preserve ConPTY processes, terminal output, scrollback, command lines, or working directories.
- Never log terminal key input, character input, pasted text, credentials, or terminal screen contents.

## Release

`windows/CmuxGui/Package.appxmanifest` is the release-version source. The public artifact is the per-user Inno Setup executable built by `windows/scripts/installer.ps1`; the MSIX scripts are for development packaging. See `docs/RELEASING.md`.

Whenever behavior, requirements, paths, installer semantics, or limitations change, update the root README, the relevant file under `docs/`, and `CHANGELOG.md` in the same change.
