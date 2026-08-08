# Architecture

cmux for Windows is a native frontend over a Rust terminal-multiplexer engine. The frontend owns Windows presentation; the engine owns terminals, resources, topology, and durable state.

## Components

```text
CmuxGui.exe (WinUI 3 / C#)
        |
        | stable C ABI via P/Invoke
        v
cmux_ffi.dll (Rust, x86_64-pc-windows-gnu)
        |
        v
cmux-tui-core
   |          |
   |          +-- Ghostty VT parser and terminal state
   +-- cmux-pty / ConPTY child processes
```

The standalone `cmux-tui.exe` uses the same core directly and exposes the local resource CLI over the Windows AF_UNIX socket implementation supplied by `uds_windows`.

## Frontend boundary

`windows/CmuxGui` renders the application shell, workspace navigation, pane layout, tabs, terminal canvas, settings, localization, and Explorer integration. It calls `cmux_ffi.dll` through declarations in `windows/CmuxGui/Interop/CmuxNative.cs`.

The FFI layer converts the native GUI's requests into core mutations and serializes snapshots that the C# layer can consume. Native allocations crossing the ABI must be released by the matching FFI function; do not free Rust-owned memory from C#.

## Authoritative topology

`cmux-tui-core` is the only authority for:

- workspaces and their ordering;
- screens and split trees;
- panes and pane tabs;
- terminal identities and lifecycle;
- active workspace, pane, and tab selection;
- durable mutation records.

The WinUI frontend projects a core snapshot into controls. Actions such as split, close, select, or create are sent back by stable public resource ID. After a mutation, the GUI reconciles from the resulting core state. It must not persist a second workspace or pane topology.

## Terminal lifecycle

A logical terminal owns a ConPTY-backed child process while the application is running. Terminal bytes are parsed by Ghostty's VT engine and rendered by the Win2D terminal control.

Workspace topology is durable; process state is not. After the GUI exits:

- workspaces, splits, panes, tabs, ordering, and active workspace can be restored;
- previous ConPTY processes are gone;
- output, scrollback, command history, command line, and process working directory are not replayed;
- each restored logical terminal starts a fresh default shell in the user's home directory.

The default shell search order is `pwsh.exe`, `powershell.exe`, then `cmd.exe`.

## Storage

| Data | Default location | Lifetime |
| --- | --- | --- |
| GUI settings | `%LOCALAPPDATA%\cmux\gui-settings.json` | Preserved across upgrades and uninstall |
| Core workspace registry | `%LOCALAPPDATA%\cmux-tui\sessions` | Preserved across upgrades and uninstall |
| TUI config | `%APPDATA%\cmux\cmux-tui.json` | User managed |
| GUI diagnostics | `%LOCALAPPDATA%\cmux-gui.log` | Append-only until the user removes it |
| Local control sockets | `%TEMP%\cmux-tui-<user>` | Runtime data |
| Browser profile, when used | `%LOCALAPPDATA%\cmux-tui\chrome-profile` | Persistent experimental data |

`CMUX_TUI_STATE_DIR` overrides the workspace-state root. `CMUX_TUI_CONFIG` overrides the TUI configuration path.

## GUI session restoration

The native GUI opens the persistent `cmux-gui` session. Its registry is stored beneath the normal session state root using a stable session hash. Closing the main window disposes live terminal views while preserving the core topology. Closing a pane tab or workspace is a durable delete and therefore changes what appears on the next launch.

A folder passed through Explorer integration applies only to the newly created workspace for that launch. It is not a promise to restore the previous process working directory later.

## Configuration and themes

The GUI stores application settings separately from the TUI configuration. Bundled Ghostty-format themes ship with the frontend, while user themes under `%APPDATA%\ghostty\themes` take priority. Ghostty configuration discovery is read-only; cmux does not require a Ghostty installation.

## Security boundaries

- Terminal keyboard and character content must never enter the diagnostic log.
- Local paths and non-sensitive errors can appear in `%LOCALAPPDATA%\cmux-gui.log`; users should review it before sharing.
- Windows permission restriction helpers do not yet provide Unix-equivalent owner-only guarantees.
- The public installer is currently unsigned and relies on the separately published SHA-256 sidecar for integrity verification.
- Remote daemon, relay, enrollment, forwarding, and cloud-machine code are intentionally outside this fork's supported architecture.
