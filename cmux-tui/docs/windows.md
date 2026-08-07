# Windows

cmux-tui runs natively on Windows as a local multiplexer. Terminals are real
ConPTY processes, the control socket is AF_UNIX via `uds_windows`, and the
public resource CLI works unchanged.

The target is `x86_64-pc-windows-gnu`. There is no MSVC-ABI build: `ghostty-vt`
is produced by Zig and linked into a GNU-ABI binary.

## Support status

| Area | Windows |
| --- | --- |
| Local multiplexer, workspaces, screens, panes, tabs | works |
| Terminals (ConPTY via `portable-pty`) | works |
| Control socket + resource CLI (`terminal`, `workspace`, …) | works |
| Browser panes (CDP) | untested |
| Remote daemon: `connect`, `ssh`, `forward`, `rpc`, `enroll` | **absent** |
| `machine-agent`, machine providers | **absent** |

Remote and machine commands are not merely broken, they are compiled out.
`cmux-remote` is declared under `[target.'cfg(unix)'.dependencies]` and uses
`tokio::net::Unix{Stream,Listener}`, which tokio does not provide on Windows.
Invoking one prints `remote daemon commands require Unix sockets and are
unsupported on windows` and exits 1.

## Prerequisites

Beyond the Zig 0.16.0 + Rust + `ghostty` submodule that every platform needs:

| Tool | Why |
| --- | --- |
| Rust with the `x86_64-pc-windows-gnu` target | the only supported Windows target |
| mingw-w64 GCC (MSVCRT flavor) | `rusqlite` is an unconditional dependency of `cmux-tui-core` and bundles SQLite C |
| libclang | `ghostty-vt-sys/build.rs` runs bindgen over `ghostty/vt.h` |

A GNU **host** toolchain (`rustup set default-host x86_64-pc-windows-gnu`)
avoids needing Visual Studio at all. An MSVC host also works and cross-builds to
the GNU target; see the linking note below, which only affects GNU hosts.

## Build

```bash
cd cmux-tui
cargo build -p cmux-tui --target x86_64-pc-windows-gnu --locked
```

### Two things that will cost you an afternoon

**1. Linking on a GNU host needs `-C link-self-contained=y`.**

With a `windows-gnu` rustup host, rustc links through the external GCC found on
`PATH`. Against a modern mingw-w64 (GCC 16) that fails with:

```
relocation truncated to fit: IMAGE_REL_AMD64_REL32 against undefined symbol `___chkstk_ms'
```

Forcing rustup's own bundled MinGW fixes it. Pass it per-crate so the dependency
cache is not invalidated:

```bash
cargo rustc -p cmux-tui --bin cmux-tui --target x86_64-pc-windows-gnu \
  -- -C link-self-contained=y
```

CI does not need this: its host is MSVC, so `host != target` and rustc already
infers self-contained linking.

**2. `BINDGEN_EXTRA_CLANG_ARGS` must use forward slashes only.**

bindgen splits that variable with shlex, which treats `\` as an escape, so
backslash paths are silently corrupted. If your libclang ships without clang's
builtin header resource directory, point it at mingw's headers:

```bash
export BINDGEN_EXTRA_CLANG_ARGS="--target=x86_64-w64-mingw32 \
  -isystem C:/mingw64/lib/gcc/x86_64-w64-mingw32/<ver>/include \
  -isystem C:/mingw64/x86_64-w64-mingw32/include"
```

Symptom when this is wrong: `fatal error: 'stddef.h' file not found`.

## Run

```bash
cmux-tui.exe                                    # TUI
cmux-tui.exe --headless --session work          # control socket only
cmux-tui.exe --session work workspace create
cmux-tui.exe --session work terminal list
```

`default_shell()` resolves `pwsh.exe`, then `powershell.exe`, then `cmd.exe`.
The control socket lives under `%TEMP%\cmux-tui-<user>\<session>.sock`; durable
state under `%LOCALAPPDATA%\cmux-tui\sessions`; config under
`%APPDATA%\cmux\cmux-tui.json`.

## Test

```bash
cargo test -p cmux-tui-core --lib --target x86_64-pc-windows-gnu --locked \
  workspace_registry::tests::
```

The full suite does not pass on Windows yet. `mux.rs` test modules hardcode
`/bin/sh` and `/tmp`, and `cmux-pty`'s tests are `#[cfg(all(test, unix))]`, so
the ConPTY backend has no automated coverage. CI runs the `workspace_registry`
subset, which covers state-root initialization: the first thing any session
does on any platform.

## Writing portable code

These are the rules that keep the Windows build working. Each exists because
breaking it produced a real bug.

- **Never `File::open` a directory.** `CreateFileW` cannot return a directory
  handle without `FILE_FLAG_BACKUP_SEMANTICS`, so the Unix "fsync the parent
  after creating an entry" idiom fails with "Access is denied". Use
  `platform::sync_directory()`.
- **Close handles before deleting.** Unix allows unlinking an open file; Windows
  returns `ERROR_SHARING_VIOLATION`. Drop registries and files before
  `remove_dir_all`, especially in tests.
- **Put platform decisions in `cmux-tui-core::platform`,** not inline `cfg`
  blocks at call sites. Paths, shell resolution, and permissions already live
  there.
- **Gate tests that touch `cfg(unix)` items.** A test referencing a Unix-only
  helper breaks the whole harness build on Windows, which silently costs all
  Windows coverage rather than one test.
- **Do not assume `rename` semantics match.** Rust's `fs::rename` on Windows maps
  to `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` and can replace an existing file
  atomically; a remove-then-rename is both unnecessary and opens a crash window.

## Known gaps

- `platform::restrict_permissions` is a no-op on non-Unix, so
  `restrict_directory` / `restrict_file` give **no** protection on Windows while
  callers assume `0700` / `0600`. This is a security gap, not a missing feature.
- `Event::EnhancedKey` is only emitted by vendored crossterm's
  `sys/unix/parse.rs`, so `base_layout_key` is always `None` on Windows and
  non-US keyboard layouts can misroute shortcuts.
- `secret_file::read_owner_only` returns `Unsupported` on Windows.
- Porting the remote subsystem means moving its transport to named pipes, which
  would also restore the peer-identity check AF_UNIX provides via
  `verify_unix_peer_owner`.
