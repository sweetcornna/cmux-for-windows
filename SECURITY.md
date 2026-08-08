# Security policy

## Supported versions

Security fixes are applied to the latest Windows release and the current default branch. Older releases may not receive backports.

This policy covers only the independently maintained Windows fork in this repository. It does not cover Manaflow's upstream cmux project, Ghostty, Windows, shells launched inside cmux, or third-party dependencies.

## Report a vulnerability

Use GitHub's private vulnerability reporting for this repository when it is available. Include:

- the affected cmux for Windows version or commit;
- the Windows version and installation method;
- a minimal reproduction;
- the expected security boundary and observed impact;
- whether the report includes secrets, personal data, or a working exploit.

If private reporting is unavailable, open a minimal issue asking the maintainer for a private contact method. Do not publish exploit code, credentials, terminal contents, or other users' data in a public issue.

Ordinary crashes, rendering problems, and non-sensitive bugs should use the bug-report template instead.

## Disclosure process

The maintainer will acknowledge a private report, reproduce and assess it, prepare a fix, and coordinate disclosure with the reporter when practical. A report may be redirected to an affected dependency or upstream project when the vulnerable code is not maintained in this fork.

## Known security limitations

- Public installers are currently unsigned. Verify the release SHA-256 sidecar before running an installer.
- `platform::restrict_permissions` does not currently enforce Unix-style `0700` or `0600` access control on Windows.
- Owner-only secret-file validation is not available on Windows.
- Browser panes are experimental and are not covered by the focused Windows CI checks.

Never attach `%LOCALAPPDATA%\cmux-gui.log` without reviewing it first. The application intentionally does not log terminal keystrokes or pasted content, but diagnostics can still contain local paths and error details.
