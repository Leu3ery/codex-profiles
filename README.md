<p align="center">
  <img src="screenshots/codex-profiles-logo.png" width="128" alt="Codex Profiles logo">
</p>

<h1 align="center">Codex Profiles</h1>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="screenshots/codex-profiles-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="screenshots/codex-profiles-light.png">
  <img alt="Codex Profiles in light mode" src="screenshots/codex-profiles-light.png">
</picture>

Native macOS and Windows manager for local ChatGPT / Codex profiles.

## Windows

This fork adds a native Windows port of version 2.0. Download the installer or portable EXE from [Releases](https://github.com/Leu3ery/codex-profiles/releases/latest). Both x64 and ARM64 builds include the .NET runtime.

See the [Windows guide](Windows/README.md) for installation, Codex CLI setup, encrypted storage, compatibility, and building from source. The original macOS app is preserved below.

## Features

- Browser sign-in, profile switching, imports, exports, and local sign-out
- Cached limits, reset counts, and manual or automatic session renewal
- Optional menu bar, ⌘0–9 switching, email privacy, and custom avatars

## Build for macOS

```bash
chmod +x Scripts/build_dmg.sh
./Scripts/build_dmg.sh
```

The app and DMG are created in `dist/`.
