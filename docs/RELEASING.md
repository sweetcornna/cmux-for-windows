# Releasing

This process publishes the independently maintained Windows build. It does not publish or modify upstream cmux releases.

## Release artifacts

A public release contains:

```text
cmux-windows-v<version>-setup.exe
cmux-windows-v<version>-setup.exe.sha256
```

The installer is self-contained, per-user, and does not require elevation. Public builds are unsigned until the maintainer has an appropriate code-signing identity.

## 1. Prepare the version and changelog

`windows/CmuxGui/Package.appxmanifest` is the installer version source and must contain a four-part numeric version such as `0.2.1.0`. Keep the first three components aligned with `cmux-tui/crates/cmux-tui/Cargo.toml` and the release tag.

Update `CHANGELOG.md` with the release date and user-visible changes. Confirm that README support claims and known limitations still match the build.

## 2. Build the release engine

Initialize the Ghostty submodule and build the GNU-ABI FFI library:

```powershell
git submodule update --init --depth 1 ghostty
rustup target add x86_64-pc-windows-gnu
cargo build --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  --release `
  --locked
```

On a GNU-host Rust toolchain, use Rust's self-contained linker components:

```powershell
cargo rustc --manifest-path .\cmux-tui\Cargo.toml `
  -p cmux-ffi `
  --target x86_64-pc-windows-gnu `
  --release `
  -- -C link-self-contained=y
```

The expected engine is `cmux-tui\target\x86_64-pc-windows-gnu\release\cmux_ffi.dll`.

## 3. Build the installer

Install Inno Setup if necessary:

```powershell
winget install --exact --id JRSoftware.InnoSetup
```

Build from the repository root:

```powershell
.\windows\scripts\installer.ps1
```

The script:

1. reads the four-part package version;
2. publishes the self-contained WinUI application;
3. copies the release GNU engine beside `CmuxGui.exe`;
4. includes `LICENSE`, `THIRD_PARTY_LICENSES.md`, and full direct Ghostty/Crossterm/terminput-crossterm license texts in the payload;
5. compiles the per-user Inno Setup installer;
6. writes the lowercase SHA-256 sidecar under `windows\dist`.

## 4. Verify the candidate

Run the focused checks from [Development](DEVELOPMENT.md), then test the installer on a clean or disposable Windows user profile.

Verify at minimum:

- SHA-256 sidecar matches the installer.
- Install completes without elevation under `%LOCALAPPDATA%\Programs\cmux`.
- Start menu shortcut launches the GUI.
- A terminal starts in the expected shell.
- Workspaces, splits, tabs, keyboard input, and mouse input work.
- Restart restores topology with fresh shells.
- Optional Explorer integration opens the selected folder.
- Upgrade preserves settings and workspace state.
- Uninstall removes application files and shortcuts but preserves user data.
- Installed `LICENSE`, `THIRD_PARTY_LICENSES.md`, and the four files under `licenses` are present.

Check the sidecar manually:

```powershell
$setup = ".\windows\dist\cmux-windows-v<version>-setup.exe"
$actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = (Get-Content "$setup.sha256").Trim().ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch" }
```

## 5. Publish

Create a `v<version>` tag from the verified commit and publish both files from `windows\dist` in the matching GitHub release. Also provide exact corresponding source for the binary release, including the pinned Ghostty submodule content and all retained build/packaging scripts; GitHub's automatically generated source archive does not include submodule contents. Release notes should include:

- a concise summary from `CHANGELOG.md`;
- minimum Windows version and x64-only status;
- the unsigned-publisher warning;
- the SHA-256 value;
- upgrade or data-migration notes;
- known limitations relevant to the release.

Do not describe the build as official, upstream-supported, or published by Manaflow.

## Development MSIX

`windows/scripts/package.ps1` and `windows/scripts/new-dev-cert.ps1` build a signed development MSIX using a certificate in the current user's certificate store. This is not the public release path. Never commit a private key or `.pfx` file.
