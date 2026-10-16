Native Windows port of Codex Profiles 2.0, based on [qwersyk/codex-profiles](https://github.com/qwersyk/codex-profiles/releases/tag/2.0).

### Downloads

- **Most Windows PCs:** `Codex-Profiles-2.0.3-windows-x64-Setup.exe`.
- **Windows on ARM:** `Codex-Profiles-2.0.3-windows-arm64-Setup.exe`.
- Portable EXEs and ZIPs are also attached. The .NET runtime is included; administrator privileges are not required.

### Features

- Save and switch local Codex accounts, browser sign-in through Codex CLI, imports/exports compatible with macOS backups, and local sign-out.
- Cached usage limits, reset times, available reset counts, and manual/automatic renewal of inactive sessions.
- System tray, Ctrl+1–9 profile shortcuts inside the app, light/dark/system appearance, email privacy, and avatar customization.
- Windows DPAPI encrypted profile vault. Outgoing sessions and rotated tokens are preserved before switching. Exported JSON backups contain credentials; keep them private.

### Verification and requirements

The Windows Actions run tests credential preservation, archive compatibility, encryption, renewal guards, and real child-process stdio RPC with synthetic credentials. It also runs the actual x64 portable and installed app, verifies account switching and email privacy, captures light/dark/compact screenshots, and tests silent install/uninstall. ARM64 is cross-built and has not been run on ARM hardware. Live browser authentication and server responses need an actual account and are not covered by these automated checks.

Windows 11 is recommended. Browser sign-in, limits and renewal require the official Windows Codex CLI (`npm install -g @openai/codex`); select a native `codex.exe` in Settings if auto-discovery fails. Switching requires file-based credential storage and closed Codex CLI sessions. WSL profiles and post-2.0 macOS Remote/live-switch features are outside this port.

The executables are unsigned. SHA-256 checksums are attached. See the [Windows guide](https://github.com/Leu3ery/codex-profiles/blob/main/Windows/README.md) for setup and storage details.
