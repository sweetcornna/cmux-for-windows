# Upstream provenance

cmux for Windows is a modified, Windows-focused fork maintained independently by `sweetcornna`.

## Source lineage

- Independent fork: <https://github.com/sweetcornna/cmux-for-windows>
- Upstream cmux source: <https://github.com/manaflow-ai/cmux>
- Upstream merge base recorded for the Windows-only repository conversion on 2026-08-08: `07322a4648a848102f487a8c3a4072f4ef57782a`
- Required Ghostty source repository: <https://github.com/manaflow-ai/ghostty>
- Ghostty submodule revision recorded for the conversion: `19d03fa4d0161e60e02de2e42601992be0c001c3`

Git history remains the authoritative record for individual files and later upstream synchronizations.

## Brand assets

The Windows executable, package, installer, title bar, and Explorer integration use the upstream cmux application icon exported at commit `3566b6ec2170fc57b74ca5b71f954aa631be75cb`. The authoritative source is `AppIcon.icon/`, and the Windows-ready rasters are derived from `Assets.xcassets/AppIcon.appiconset/` at that revision.

## Nature of this fork

This repository keeps the native Windows GUI, Windows-compatible Rust multiplexer and TUI, Ghostty terminal-engine source, Windows packaging, CI, and project documentation. The conversion removed upstream Apple applications, Swift/Xcode sources, iOS support, web applications, cloud services, Homebrew distribution, Unix remote daemon and relay, language SDK publishing, upstream marketing assets, and internal project tooling.

The Windows frontend and packaging are modifications maintained in this fork. Manaflow does not publish, endorse, or support the resulting Windows binaries. Issues specific to this fork belong in the fork's issue tracker.

## Copyright and licenses

Original authors and contributors retain their copyrights. [LICENSE](LICENSE) is preserved as the repository's legal notice and default license statement. Third-party and file-level licenses continue to apply; see [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md).

The Rust workspace manifests retain an inherited `license = "MIT"` declaration while the root license applies GPL-3.0-or-later by default where no other notice applies. This provenance document does not attempt to relicense any code. Any future license-scope cleanup must be based on verified source history and contributor authority, not on the fork maintainer's assumption.

## Updating from upstream

An upstream synchronization should be selective. Import only changes needed by retained Windows components, preserve upstream authorship in Git history, update the recorded dependency revisions when applicable, and do not reintroduce deleted product surfaces merely to simplify a merge.
