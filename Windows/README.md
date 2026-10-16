# Codex Profiles for Windows

A native WPF port of the macOS Codex Profiles 2.0 profile manager. The original Swift app remains in `Sources/`; the Windows implementation is in `Windows/`.

## Download and run

Download the installer from [this fork's releases](https://github.com/Leu3ery/codex-profiles/releases/latest):

- Most PCs: `Codex-Profiles-2.0.1-windows-x64-Setup.exe`.
- Windows on ARM: `Codex-Profiles-2.0.1-windows-arm64-Setup.exe`.
- A portable `.exe` and ZIP are also available for each architecture.

Run the installer, then open **Codex Profiles** from the Start menu. Installation is per-user and does not require administrator privileges. The app includes its .NET 10 runtime. Windows 11 is recommended. The executables are not Authenticode-signed, so Windows may show a publisher/reputation prompt. SHA-256 checksums are provided with the release.

## Using profiles

1. **Save current** saves `%USERPROFILE%\.codex\auth.json` (or the directory selected by `CODEX_HOME`).
2. **Add account** signs into an additional account through the official Codex CLI and your browser, without replacing the active session.
3. Select a profile and choose **Switch account**, or press **Ctrl+1–9** in the app. The order follows the sort picker. **Ctrl+0** opens the main window while it has keyboard focus; shortcuts are not global system hotkeys.
4. Import `auth.json` or a macOS/Windows `.codexprofile.json` / `.codexprofiles.json` backup using **Import** or drag and drop. Exports use the original `codex-profiles-archive` version 1 format.
5. Check limits, view cached remaining usage/reset times and available reset credits, or renew an inactive session. Settings controls automatic renewal (enabled by default), renewal lead time, email privacy, appearance, and the system tray.

When switching, the app asks a running Codex desktop window to close, waits for its processes to stop, saves the outgoing session, changes only `auth.json`, and restarts the desktop if it was running. It does not force-kill Codex. Close terminal Codex sessions before switching. Projects, conversation databases and `config.toml` are not copied or replaced. **Sign out locally** saves the current account before removing local active credentials; it does not revoke the session on the server. Removing a saved profile leaves the active session intact.

If `cli_auth_credentials_store` is explicitly configured, it must be `"file"` at the root of Codex `config.toml`. The app reports incompatible credential storage and leaves the config unchanged. Set it yourself if needed:

```toml
cli_auth_credentials_store = "file"
```

## Codex CLI

Browser login, limit checks and session renewal need a current native Windows `codex.exe`. Install the official CLI with:

```powershell
npm install -g @openai/codex
```

Reopen Codex Profiles after installation. Discovery supports native `codex.exe` on `PATH` and npm's Windows native package layout. If it is not found, choose the native executable in **Settings**, or set `CODEX_CLI_PATH`. Do not select the npm `codex.cmd` shim. Profile save/import/export/switching work without a CLI installation.

An optional desktop executable override is available in Settings; normally the running desktop's executable is discovered automatically. Browser authentication and RPC use Codex's own authentication implementation, rather than a separate OAuth client.

## Storage and renewal

- Saved profiles, including their metadata, are encrypted with Windows DPAPI for the current Windows user in `%LOCALAPPDATA%\Codex Profiles\profiles.dat`.
- The app's directory is restricted to the current user and SYSTEM. Temporary authentication workspaces live under that directory and are removed after each operation.
- The active `auth.json` remains in Codex's standard file format. **Exported JSON backups contain credentials in plaintext**; keep them private. Use export/import to migrate to another computer, rather than copying the DPAPI vault.
- Refreshed credentials are synchronized before switching/exporting. Older imported credentials cannot overwrite a newer snapshot with a known refresh date.
- Renewal only operates on inactive sessions with independent refresh tokens. The active account is managed by Codex. Duplicate refresh tokens and incompatible storage are rejected. Permanent refresh failures pause automatic attempts until a new sign-in; retries are at least 24 hours apart. Rotated tokens are retained even if a request subsequently times out.
- Automatic maintenance runs once a minute while the manager is open (including in the tray); it renews due inactive sessions and refreshes the active account's cached limits. Closing via **Quit** stops maintenance.
- Session renewal does not extend a subscription. API key profiles can be saved/switched but do not expose ChatGPT account limits or managed session renewal.

`CODEX_PROFILES_HOME` optionally changes the manager's storage directory. `CODEX_HOME` selects the native Windows Codex home. This release does not manage a separate WSL filesystem, and does not port the post-2.0 macOS Remote or live-switch features.

## Build and test

Install the .NET 10 SDK and Inno Setup 6 on Windows:

```powershell
dotnet run --project Windows/CodexProfiles.Tests -c Release
./Scripts/build_windows.ps1 -Architecture x64
./Scripts/build_windows.ps1 -Architecture arm64
```

Outputs are in `dist/windows-x64` and `dist/windows-arm64`. The regression harness also runs on macOS/Linux with .NET 10; DPAPI and Windows ACL behavior is exercised on Windows CI. WPF can be cross-compiled with `EnableWindowsTargeting`, but must be run on Windows.

The **Windows** GitHub Actions workflow runs storage/auth/RPC regression tests, creates self-contained portable executables and Inno Setup installers, and runs the actual x64 packaged and installed app with isolated synthetic accounts. Its UI smoke test checks saving, switching, privacy and light/dark/compact rendering. Screenshots are retained as Actions artifacts. ARM64 is cross-built; it has not been smoke-tested on ARM hardware. Real browser login and account-server behavior require an actual user account and are not exercised by CI.

Pushing a `windows-v*` tag publishes both architectures to GitHub Releases only after all build and verification jobs succeed. Release assets include installer and portable EXEs, ZIPs, and checksums.
