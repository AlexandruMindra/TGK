# Changelog

All notable changes to TGK are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/)
(see [docs/RELEASING.md](docs/RELEASING.md)).

Add every user-visible change under **Unreleased** in the same pull request as the change. When a version is released,
that section is renamed to the version and dated; its text becomes the GitHub release notes, and the nightly build
shows the current Unreleased section.

## [Unreleased]

### Added

- **Split view**: show several sessions at once. The layout button in the tab strip offers 2 or 3 side by side,
  2 stacked, 1 large + 2 stacked or below, and grids of 4 or 6; any pane can also be split right or down
  (`Ctrl+Shift+E` / `Ctrl+Shift+O`). Drag the dividers to resize (double-click evens them out), `Alt+arrows` move
  between panes, `Ctrl+Shift+X` maximizes a pane. Each pane stays a tab of its own.
- **Reopen my tabs** (Settings → Startup, off by default): tabs and split views are saved in the vault when TGK
  closes, signs out or locks (and as you work) and reopened on whichever device you sign in next. Only which hosts
  were open is saved, never passwords.
- **Updates**: a while after start, and about twice a day, TGK checks GitHub for a newer release and says so in a
  small note in the status bar. "Update now" downloads it in the background, verifies it against GitHub's checksum and
  installs it when TGK restarts or closes. "Check for updates" in the account menu asks right away.
- **macOS builds** for Apple Silicon (`TGK-osx-arm64`) and Intel (`TGK-osx-x64`), as a `.dmg` and as a `TGK.app`
  archive. `⌘` works like `Ctrl` (⌘C / ⌘V copy and paste in the terminal), and the system file dialogs are used.
- **One-file downloads**: a Windows installer (`TGK-win-x64-setup.exe`, per user, no administrator rights) and a Linux
  AppImage (`TGK-x86_64.AppImage`). The archives stay available for portable use.
- Tab strip: when not all tabs fit, `‹ ›` arrows (hold to keep scrolling) and a list of all tabs; right-click a tab
  for its menu.

### Fixed

- The tab strip could not be scrolled to tabs out of view: any title or status change scrolled it back to the active
  tab. Horizontal (touchpad) scrolling works too.
- Replacing a tab that sits before the active tab (e.g. connecting from a new-tab page) no longer shifts which tab is
  active.

## [0.1.0] - 2026-09-29

First release.

### Added

- Browser-style tabs of SSH terminals with quick connect (`user@host:port`) and saved hosts.
- Built-in xterm-compatible terminal: 256 colors and truecolor, alternate screen, scrollback, mouse reporting,
  bracketed paste, wide characters, selection with copy/paste.
- Authentication with private keys, passwords or keyboard-interactive; host key verification.
- Hosts with groups, search, tag colors and shared identities; per-host options with inheritance: jump hosts, keep-alive,
  automatic reconnect, port forwarding (local, remote, SOCKS), startup command, environment, terminal type, font
  size, color schemes, legacy algorithms.
- End-to-end encrypted vault: a self-hosted sync server (with TOTP sign-in, devices, password change) or a local
  vault on one device; moving between the two; encrypted backups.
- Linux and Windows builds.

[Unreleased]: https://github.com/AlexandruMindra/TGK/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/AlexandruMindra/TGK/releases/tag/v0.1.0
