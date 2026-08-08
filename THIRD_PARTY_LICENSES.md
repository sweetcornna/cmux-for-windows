# Third-party notices

cmux for Windows is derived from upstream cmux and uses third-party source and binary dependencies. The repository license and upstream copyright notice are in [LICENSE](LICENSE). Package manifests and lockfiles are authoritative for the exact dependency versions in a build.

This file identifies source copied into or built directly from this repository. It does not replace license files distributed with dependency packages.

## Project license scopes

The root [LICENSE](LICENSE) states the repository's default GPL-3.0-or-later terms where no other file or notice applies. The retained Rust workspace manifests inherit `license = "MIT"` from `cmux-tui/Cargo.toml`. This file records that inherited metadata but does not establish a new license grant or resolve ownership by assumption; source history and file-level notices remain authoritative.

## Ghostty

The terminal parser is built from the `ghostty` submodule.

- Project: Ghostty
- Upstream source: <https://github.com/ghostty-org/ghostty>
- Repository source: <https://github.com/manaflow-ai/ghostty>
- License: MIT
- Copyright: Copyright (c) 2024 Mitchell Hashimoto and Ghostty contributors
- Full text in source: `ghostty/LICENSE`
- Full text in an installed build: `licenses/Ghostty-MIT.txt`

## Crossterm

The repository carries a patched Crossterm source tree under `cmux-tui/vendor/crossterm` so shifted keys, base-layout keys, and associated text survive the input adapter.

- Project: Crossterm
- Source: <https://github.com/crossterm-rs/crossterm>
- License: MIT
- Copyright: Copyright (c) 2019 Timon
- Full text in source: `cmux-tui/vendor/crossterm/LICENSE`
- Full text in an installed build: `licenses/Crossterm-MIT.txt`

## terminput-crossterm

The repository carries a patched `terminput-crossterm` adapter under `cmux-tui/vendor/terminput-crossterm` to map the extended Crossterm event shape.

- Project: terminput
- Source: <https://github.com/aschey/terminput>
- License: MIT OR Apache-2.0
- Copyright: Austin Schey and contributors
- Full texts in source: `cmux-tui/vendor/terminput-crossterm/LICENSE-MIT` and `cmux-tui/vendor/terminput-crossterm/LICENSE-APACHE`
- Full texts in an installed build: `licenses/terminput-crossterm-MIT.txt` and `licenses/terminput-crossterm-Apache-2.0.txt`

## Bundled themes and icons

The Windows frontend includes Ghostty-format theme files and cmux application icons under `windows/CmuxGui/Assets`. These assets entered this fork with the native Windows frontend. Their names identify visual schemes but do not, by themselves, establish authorship or license terms. Before publishing a release with a newly added or replaced asset, record whether it is original, inherited from upstream cmux or Ghostty, or imported from another project, and include any required attribution and redistribution terms.

## Rust and .NET dependencies

Rust dependencies are declared in `cmux-tui/Cargo.toml` and locked in `cmux-tui/Cargo.lock`. .NET dependencies are declared in `windows/CmuxGui/CmuxGui.csproj` and restored by NuGet. Their authors retain all applicable rights, and each dependency remains subject to its own license.

The installer includes [LICENSE](LICENSE), this notice, and the full license texts for the directly vendored Ghostty, Crossterm, and terminput-crossterm source. Before each public release, the maintainer must also audit the resolved Rust graph, self-contained .NET runtime, Windows App SDK, Win2D, copied publish payload, themes, and icons for additional notice or source-distribution requirements. A generated dependency report or software bill of materials can support that audit but does not replace required license text.

A maintainer adding a vendored source tree, bundled asset, or binary dependency must add its required attribution and license text before publishing a release.
