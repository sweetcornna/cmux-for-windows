# Contributing to cmux for Windows

Thank you for contributing to this independent Windows fork. This repository is maintained by `sweetcornna` and is not an official Manaflow support channel.

## Scope

Changes should support the native Windows GUI, the Windows-compatible Rust engine, the standalone Windows TUI, or their documentation and release tooling. macOS, iOS, web, cloud-machine, Unix remote-daemon, and Homebrew functionality are intentionally outside this repository's scope.

Before starting a large feature, open an issue describing the user problem and the proposed Windows behavior. Bug fixes and focused documentation corrections can be submitted directly.

## Set up

1. Use 64-bit Windows 10 version 2004 or newer.
2. Clone the repository with submodules.
3. Install the Rust, Zig, LLVM/Clang, MinGW-w64, and .NET 8 prerequisites listed in [Development](docs/DEVELOPMENT.md).
4. Build the Rust engine before building the WinUI application.

```powershell
git clone --recurse-submodules https://github.com/sweetcornna/cmux-for-windows.git
Set-Location cmux-for-windows
rustup target add x86_64-pc-windows-gnu
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-ffi --target x86_64-pc-windows-gnu --locked
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

## Development rules

- Keep platform decisions in `cmux-tui-core::platform` rather than scattering conditional compilation through call sites.
- Never open a directory with `File::open` to synchronize it on Windows; use `platform::sync_directory()`.
- Drop open files, registries, and native handles before deleting their paths.
- Keep the engine on the `x86_64-pc-windows-gnu` ABI. Do not introduce an MSVC engine artifact with the same name.
- Treat the Rust core as the source of truth for workspace topology. The WinUI frontend must not maintain a second topology database.
- Never log terminal key input, pasted text, tokens, passwords, or shell contents.
- Update user documentation and `CHANGELOG.md` when behavior, requirements, file locations, or release steps change.
- Do not commit `bin`, `obj`, `target`, `windows/dist`, private certificates, or generated installer payloads.

## Tests

Run the focused Windows checks before opening a pull request:

```powershell
cargo fmt --manifest-path .\cmux-tui\Cargo.toml --all -- --check
cargo build --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui -p cmux-ffi --target x86_64-pc-windows-gnu --locked
cargo test --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui-core --lib --target x86_64-pc-windows-gnu --locked workspace_registry::tests::
cargo test --manifest-path .\cmux-tui\Cargo.toml -p cmux-tui-core --lib --target x86_64-pc-windows-gnu --locked persistent_gui_restart_restores_terminal_cwds_and_agent_sessions
dotnet build .\windows\CmuxGui\CmuxGui.csproj -c Debug -r win-x64
```

For GUI changes, also verify keyboard and mouse input, pane focus, split/tab operations, settings persistence, app restart behavior, and the affected English and Simplified Chinese strings.

## Pull requests

Keep pull requests focused. Include:

- the user-visible problem and resulting behavior;
- the exact commands used for verification;
- screenshots or a short recording for visible GUI changes;
- documentation and changelog updates when applicable;
- any known limitation that remains.

Contributions follow the license already applicable to the path being changed. The root [LICENSE](LICENSE) is the default statement; Rust workspace manifests retain an inherited MIT declaration, and third-party code remains under its original license. Do not remove notices or assume that a contribution can relicense existing code. Raise unclear scope in an issue before making a license-only change.
