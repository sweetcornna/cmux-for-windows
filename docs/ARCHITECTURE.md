# Architecture

cmux for Windows is a native frontend over a Rust terminal-multiplexer engine. The frontend owns Windows presentation; the engine owns terminals, resources, topology, and durable state.

## Components

```text
CmuxGui.exe (WinUI 3 / C#)
   |          |
   |          +-- WebView2 browser controls
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

`windows/CmuxGui` renders the application shell, workspace and screen navigation, pane layout, tabs, the DirectWrite/Direct2D terminal surface hosted by Win2D, WebView2 browser controls, settings, localization, and Explorer integration. It calls `cmux_ffi.dll` through declarations in `windows/CmuxGui/Interop/CmuxNative.cs`.

The FFI layer converts the native GUI's requests into core mutations and serializes snapshots that the C# layer can consume. Native allocations crossing the ABI must be released by the matching FFI function; do not free Rust-owned memory from C#. The GUI publishes the DirectWrite cell size in physical pixels to the mux, which applies it to live surfaces and uses it for new ConPTY geometry; this keeps terminal pixel queries aligned with the grid drawn at the current display DPI.

## Authoritative topology

`cmux-tui-core` is the only authority for:

- workspaces and their ordering;
- screens and split trees;
- panes and pane tabs;
- terminal identities and lifecycle;
- browser identities and durable navigation metadata;
- active workspace, screen, pane, and tab selection;
- durable mutation records.

The WinUI frontend projects a core snapshot into controls. Actions such as split, close, select, or create are sent back by stable public resource ID. After a mutation, the GUI reconciles from the resulting core state. It must not persist a second workspace or pane topology. Periodic volatile status updates reuse the existing terminal, browser, tab, and pane controls; only structural topology changes rebuild the workspace layout. Reused terminal controls receive a one-shot post-arrange grid synchronization and forced snapshot repaint after reparenting so the Win2D backing surface matches the final pane size. A terminal host joins the tab tree before its `CanvasControl`; the canvas is attached only after the selected host finishes loading, avoiding WinUI's unmatched early unload event that otherwise prevents Win2D resource creation.

## Terminal lifecycle

A logical terminal owns a ConPTY-backed child process while the application is running. Terminal bytes are parsed by Ghostty's VT engine into a fixed cell grid. The WinUI terminal control renders that grid through Win2D's DirectWrite/Direct2D surface: each grapheme is shaped independently and clipped to its Ghostty-assigned one- or two-cell span, while the frontend composes selection, blink, text decorations, and cursor visuals. The renderer never owns a second PTY or VT parser.

Workspace topology is durable; process state is not. After the GUI exits:

- workspaces, splits, panes, tabs, ordering, and active workspace can be restored;
- previous ConPTY processes are gone;
- output, scrollback, command history, command line, and process working directory are not replayed;
- each restored logical terminal starts a fresh default shell in the user's home directory.

The default shell search order is `pwsh.exe`, `powershell.exe`, then `cmd.exe`.

## Browser lifecycle

The Rust core owns each browser's stable resource ID, tab placement, current URL, and durable topology. The WinUI frontend owns the live WebView2 control and sends completed navigation URLs back through the same stable-ID mutation boundary. Browser controls are reused while topology is reconciled, so navigation history and page state survive unrelated sidebar and layout updates during the current process.

WebView2 state and browsing history are live frontend state. Restart restores the browser tab and its last durable URL, not the previous WebView process, in-page state, form data, or back/forward history. The standalone TUI retains its separate experimental Chrome DevTools Protocol transport; the native GUI does not require a Chrome debug profile.

## Storage

| Data | Default location | Lifetime |
| --- | --- | --- |
| GUI settings | `%LOCALAPPDATA%\cmux\gui-settings.json` | Preserved across upgrades and uninstall |
| Core workspace registry | `%LOCALAPPDATA%\cmux-tui\sessions` | Preserved across upgrades and uninstall |
| TUI config | `%APPDATA%\cmux\cmux-tui.json` | User managed |
| GUI diagnostics | `%LOCALAPPDATA%\cmux-gui.log` | Append-only until the user removes it |
| Local control sockets | `%TEMP%\cmux-tui-<user>` | Runtime data |
| TUI Chrome debug profile, when the experimental CDP transport is used | `%LOCALAPPDATA%\cmux-tui\chrome-profile` | Persistent TUI-only data |

`CMUX_TUI_STATE_DIR` overrides the workspace-state root. `CMUX_TUI_CONFIG` overrides the TUI configuration path.

## GUI session restoration

The native GUI opens the persistent `cmux-gui` session. Its registry is stored beneath the normal session state root using a stable session hash. Closing the main window disposes live terminal and browser views while preserving the core topology. Closing a pane tab, screen, or workspace is a durable delete and therefore changes what appears on the next launch.

A folder passed through Explorer's new-workspace command is forwarded to the running main instance and creates a workspace there. The new-window command opens an independent transient mux that does not create a durable session registry. A launch folder applies only to the newly created workspace for that launch; it is not a promise to restore the previous process working directory later.

## Configuration and themes

The GUI stores application settings separately from the TUI configuration. Bundled Ghostty-format themes ship with the frontend, while user themes under `%APPDATA%\ghostty\themes` take priority. Ghostty configuration discovery is read-only; cmux does not require a Ghostty installation.

`AppSettings` composes the selected theme and terminal foreground/background overrides into a Ghostty-format fragment. The frontend sends that fragment once to the Rust mux, whose `DefaultColors` update every existing surface and become the defaults for future surfaces. Sending an empty fragment reloads the user Ghostty configuration and built-in defaults, so Follow config and reset operations also update already-open terminals.

The application accent is a shared mutable `SolidColorBrush` initialized after the first window's XAML tree exists. Explicitly supported shell actions and settings controls retain that brush instance, so changing its color repaints them immediately without replacing WinUI resource dictionaries or forcing a theme transition.

The settings surface scrolls vertically, constrains its content to the current viewport width, and reserves bounded columns for right-aligned actions. Resizing or maximizing the window therefore keeps those controls inside the card instead of creating clipped horizontal overflow.

## Security boundaries

- Terminal keyboard and character content must never enter the diagnostic log.
- Local paths and non-sensitive errors can appear in `%LOCALAPPDATA%\cmux-gui.log`; users should review it before sharing.
- Windows permission restriction helpers do not yet provide Unix-equivalent owner-only guarantees.
- The public installer is currently unsigned and relies on the separately published SHA-256 sidecar for integrity verification.
- Remote daemon, relay, enrollment, forwarding, and cloud-machine code are intentionally outside this fork's supported architecture.
