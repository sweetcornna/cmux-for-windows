# cmux TUI for Windows

`cmux-tui` is the Rust terminal-multiplexer engine, standalone text interface, and local resource CLI used by cmux for Windows. It runs real ConPTY child processes and parses terminal state with Ghostty's VT engine.

This directory is part of `sweetcornna`'s independent Windows fork. Unix remote-daemon, relay, cloud-machine, web frontend, and multi-language SDK publishing components are intentionally not included.

## Supported target

The only supported engine target is:

```text
x86_64-pc-windows-gnu
```

There is no supported `x86_64-pc-windows-msvc` artifact.

## Build

Initialize the Ghostty submodule from the repository root, then build the TUI and GUI FFI library:

```powershell
git submodule update --init --depth 1 ghostty
rustup target add x86_64-pc-windows-gnu
cargo build --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-tui -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  --locked
```

The build requires Rust 1.91 or newer, Zig 0.16.0, MinGW-w64, and libclang. See [Development](../docs/DEVELOPMENT.md) for setup and linker details.

## Run

```powershell
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe --session agents
.\cmux-tui\target\x86_64-pc-windows-gnu\debug\cmux-tui.exe --headless --session agents
```

The default session is `main`. Detach from an attached TUI with the configured prefix followed by `d`; the default is `Ctrl-b d`.

The shell search order is `pwsh.exe`, `powershell.exe`, then `cmd.exe`.

## Local resource CLI

The noun-first CLI controls a local session over its Windows AF_UNIX socket:

```powershell
cmux-tui.exe --session agents workspace create --name api
cmux-tui.exe --session agents workspace list
cmux-tui.exe --session agents terminal list
cmux-tui.exe --session agents terminal <terminal-id> screen read
```

Resource IDs are opaque typed strings. Selectors can also use `current` or an exact name. Duplicate names return an ambiguity error; use the stable ID to disambiguate.

Remote commands such as `connect`, `ssh`, `forward`, `rpc`, and `enroll` are unavailable in this Windows-only fork.

## Paths

| Purpose | Default |
| --- | --- |
| Session state | `%LOCALAPPDATA%\cmux-tui\sessions` |
| Configuration | `%APPDATA%\cmux\cmux-tui.json` |
| Control sockets | `%TEMP%\cmux-tui-<user>\<session>.sock` |
| Browser profile | `%LOCALAPPDATA%\cmux-tui\chrome-profile` |

Use `CMUX_TUI_STATE_DIR` to override the state root and `CMUX_TUI_CONFIG` to override the configuration file.

## Persistence

The core persists workspace, split, pane, tab, and active-workspace topology. It does not preserve ConPTY process state, terminal output, scrollback, commands, or working directories. Restored logical terminals start fresh default shells.

## Test

```powershell
cargo fmt --manifest-path .\cmux-tui\Cargo.toml --all -- --check
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
```

The entire inherited test suite is not Windows-portable; some retained tests still assume `/bin/sh` and `/tmp`.

## Crates retained in this fork

| Crate | Role |
| --- | --- |
| `cmux-ffi` | C ABI for the WinUI frontend |
| `cmux-tui` | Standalone TUI and local CLI |
| `cmux-tui-core` | Resource model, control socket, persistence, and terminal lifecycle |
| `cmux-pty` | PTY abstraction and ConPTY backend |
| `cmux-tui-cdp` | Experimental browser-pane transport |
| `ghostty-vt` / `ghostty-vt-sys` | Ghostty terminal parser bindings and build integration |
| `cmux-tui-machine-protocol` | Shared machine-list protocol types still required by the Windows TUI build |

For architecture and persistence details, see [Architecture](../docs/ARCHITECTURE.md).
