# Changelog

All notable changes to TGK are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/)
(see [docs/RELEASING.md](docs/RELEASING.md)).

Add every user-visible change under **Unreleased** in the same pull request as the change. When a version is released,
that section is renamed to the version and dated; its text becomes the GitHub release notes, and the nightly build
shows the current Unreleased section.

## [Unreleased]

### Added

- **Retro color schemes**: Amber CRT (an amber phosphor monitor: every color a shade of amber, told apart by
  brightness), Commodore 64 (its 16 colors on the blue screen), MS-DOS (IBM CGA's colors on black), Synthwave '84
  (80s neon) and Teletype (ink on paper).
- **VT323 font** (after the DEC VT320's) is bundled and offered with the terminal fonts; it suits the retro schemes.
  It is scaled so a font size looks alike in it and in DejaVu Sans Mono, and bold text is drawn bolder.

### Changed

- **Update check**: TGK now asks GitHub for a newer version on every start, in the background as it starts (nothing
  waits for the answer; the last one shows until it arrives), instead of at most twice a day and only 45 seconds after
  the window opened. A new nightly build is seen at the next start.
- **Color schemes**: the scheme shown on the first card (the default, or the one inherited from the group or the
  defaults) is no longer offered a second time; TGK Dark no longer appears twice in Settings.

### Fixed

- **Settings, New host / Edit host, group settings**: the first of these dialogs in a run no longer freezes the window
  for seconds (and no longer adds hundreds of MB of memory) on systems with many fonts installed, as is common on
  Linux. The terminal font list now opens only the fonts whose name suggests a terminal font, and is built in the
  background at startup; a font chosen on another device and installed here still works even if it is not listed.
- **High-resolution displays**: TGK now follows the display's scale (Windows' Scale setting, e.g. 175% on a 4K
  screen; Xft.dpi on Linux). Text and everything else were drawn at 100% there and looked tiny. The window opens at
  the scaled size, and moving it to a display with another scale, or changing the scale while TGK runs, rescales the
  UI. macOS already scaled correctly. (Blossom 0.1.8, the UI library, now does this itself.)
- **Sharper text and lines**: text in the UI and the terminal is now drawn with subpixel antialiasing (like ClearType
  on Windows) instead of grayscale, which looked blurry next to other apps. At a fractional display scale (e.g. 125%)
  borders, separators, the text cursor and icons are aligned to whole screen pixels, so a 1px line is no longer
  smeared over two. (Blossom 0.1.9; macOS keeps grayscale text, as macOS itself does.)

## [0.3.0] - 2026-10-06

### Added

- **Agents (MCP)**: Claude Code and other MCP clients can work on your saved hosts through TGK: run commands, read,
  search and edit files, copy files up and down. Turn it on in Settings → Agents (off by default) and register TGK
  with Claude Code once (the tab gives the command); TGK must be running. Hosts stay closed to agents until you open
  them, per host, per group or for all hosts, in the new Agents tab: Read only, Ask (you approve every change in TGK,
  with the exact command or a diff of the file) or Full, plus commands that may run without asking and paths that
  always need approval (keys and other secrets are protected out of the box). Commands as root (sudo) are always
  approved by you, and TGK supplies the password. Agents never see passwords or keys; the status bar shows when they
  are connected, and every call is listed in the activity view, mirrored in an "Agent log" tab and kept in an audit
  log. See docs/AGENTS.md.
- **Files (SFTP)**: a Files tab per host (right-click a host → *Browse files*, or *Browse files* in a terminal's
  menu). Browse folders (back, forward, up, a path to type, a name filter, hidden files with Ctrl+H, sortable columns),
  upload files and folders (pick them or drop them on the window), download them to the Downloads folder or anywhere,
  with a transfer panel (progress, speed, time left, cancel) and Replace / Skip for names that already exist. Open or
  edit a file in a local program: each save is uploaded back. New folder or file, rename, move, delete, permissions,
  copy paths, open a terminal in a folder. Connects through the host's jump hosts with its credentials (a one-time
  code is asked once); symbolic links are shown with their target, and deleting or renaming one never touches what it
  points to. Files tabs are reopened at their folder with "Reopen my tabs" (older versions open them as terminals).
- **Files: split view**: open a pane beside a Files tab (toolbar → panes, or *More*): another folder of the same host
  (same connection, no second sign-in), this computer's files, or another saved host. Copy or move between panes by
  dragging (onto the pane or one of its folders; Shift moves, Ctrl copies), with `F5` / `F6`, or from the context
  menu, after a confirmation. Within one host a move is a rename. Between two hosts you choose the route: *Through
  this computer* (each file goes through a private local buffer, the hosts never connect to each other) or *Directly
  between the hosts* (host A copies to host B over SSH with B's vault credentials, checked against B's trusted host
  key, nothing left on A; B's key works in any format the vault takes — PuTTY, PEM, PKCS#8, with a passphrase — as it
  is converted for ssh on host A). Panes refresh when a transfer into them finishes; local panes are reopened too.
- **Files: built-in text editor**: text files open in TGK itself, beside the file pane (double-click or Enter, *Edit*
  / F4, or *View* / F3 for read only), without downloading them: line numbers, undo and redo, find and replace
  (Ctrl+F, Ctrl+H), go to line (Ctrl+G), Tab indents (with the file's own tabs or spaces), the file's encoding and line
  endings kept. Ctrl+S saves straight to the host: written beside the file and put in its place, keeping its
  permissions and owner, after checking that nobody changed it meanwhile. Files you can't read or write are opened and
  saved as root with sudo when you ask (the password is asked for, never stored). Logs (`*.log`, `/var/log`) open read
  only at their end and follow what is added; files over 8 MB show their last 2 MB. Closing a tab, signing out or
  quitting with unsaved changes asks first. Other files (images, archives, documents…) still open in a program here.

### Fixed

- **Windows, macOS**: resizing the window by dragging its edge now repaints the content live, instead of only once the
  mouse button is released.

## [0.2.4] - 2026-10-04

### Added

- **Generate SSH keys** (Keys & identities → *Generate…*): a new Ed25519 key (recommended) or RSA 4096 key (for older
  servers) is created on the device and kept in the vault with the identity.
- **Copy public key** for any identity's private key, as an `authorized_keys` line to add on your servers.
- **Keys found on this device**: when an identity has no key yet, the private keys already on the device are listed so
  one click loads them. Only the usual places are searched, never the whole disk: `~/.ssh` (and its subfolders), the
  `IdentityFile`s of `~/.ssh/config` and the keys used by saved PuTTY and WinSCP sessions; *Search Downloads, Desktop,
  Documents* also looks at the top level of those folders. Encrypted keys are listed without asking for their
  passphrase, and keys already in the vault are marked. Nothing leaves the device until you save the identity.
- **Remove a loaded key** (× in the key box) to pick another one, e.g. from the keys found on this device.

## [0.2.3] - 2026-10-03

### Changed

- TGK now uses the UI framework Blossom from its NuGet package (0.1.1) instead of a copy built from source, with
  SkiaSharp 4 and Silk.NET 2.23 underneath. The screens look and behave as before, with these differences:
  - Held keys repeat at your system's keyboard repeat delay and rate (TGK used a fixed 400 ms delay, then 30 per
    second).
  - Less CPU and battery use while TGK sits idle: the window now sleeps until there is input, terminal output or
    something to animate, instead of waking about a thousand times a second.
- Updated SkiaSharp (2.88.3 → 4.153) and ImageSharp (2.1.2 → 3.1.12), which had known high-severity vulnerabilities
  ([GHSA-j7hp-h8jx-5ppr](https://github.com/advisories/GHSA-j7hp-h8jx-5ppr),
  [GHSA-65x7-c272-7g7r](https://github.com/advisories/GHSA-65x7-c272-7g7r)).

## [0.2.2] - 2026-10-01

### Added

- **Move panes by drag and drop**: drag a pane's title bar (or a tab from the tab strip) over the split view. Near an
  edge of a pane it goes beside that pane on that side (left, right, above or below); over the middle of a pane of the
  same split view the two swap places; onto the tab strip it becomes a tab of its own. Without a split view on screen,
  dropping a tab at an edge of the current tab starts one. The drop zone is highlighted while dragging.
- **Update channel** (Settings → Startup, per device): *Stable* (releases only) or *Nightly* (the latest build of
  `main`, plus releases when they are newer). Switching back to Stable never downgrades: a nightly build stays until a
  release newer than it comes out. By default the channel follows the installed build.

### Changed

- A tab in the tab strip is activated when the mouse button is released (unless it was dragged), so dragging a tab
  that has not connected yet is no longer interrupted by its password prompt.

## [0.2.1] - 2026-10-01

### Added

- **Drag tabs** in the tab strip: drop a tab between two others to move it there; drop it onto the middle of another
  tab to show the two side by side (a split view). Dropped between two panes of a split view it joins that split view;
  dragged out of its split view's tabs it becomes a tab of its own again. Two panes of the same split view dropped
  onto each other swap places.
- **Search saved hosts from the new-tab page**: what you type in the connect bar also searches your saved hosts by
  name, user and/or host (`root@`, `@10.0`, `deploy@web`, a group name…). `↓`/`↑` choose a match and Enter connects
  to it; an address typed in full that belongs to a saved host connects with that host's settings.
- **Terminal font**: choose the bundled DejaVu Sans Mono or any monospace font installed on the device, in Settings →
  Terminal and per group or host (Appearance). A device without the chosen font uses the bundled one.
- **Hidden sidebar opens on hover**: with the sidebar hidden, rest the pointer at the window's left edge to show it
  over the content; it hides again when the pointer leaves it.

### Changed

- **Terminal settings sync**: font, font size, color scheme, scrollback, cursor style, blinking and copy-on-select are
  now stored in your vault and follow you to every device (or stay in your local vault). The first device that starts
  this version moves its own terminal settings into the vault, unless the vault already has some.
- Settings: the color scheme and font size moved from the Appearance tab to the Terminal tab, next to the new font
  choice; the Appearance tab is gone. "Legacy algorithms" moved to the Connection tab (in Settings, group settings
  and the host editor).

## [0.2.0] - 2026-09-30

Coming from 0.1.0? It can't update itself: download this version once, and from here on TGK updates in place.

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
- **macOS builds** (preview) for Apple Silicon (`TGK-osx-arm64`) and Intel (`TGK-osx-x64`), as a `.dmg` and as a
  `TGK.app` archive. `⌘` works like `Ctrl` (⌘C / ⌘V copy and paste in the terminal), and the system file dialogs are
  used. The app is not notarized by Apple yet: open it the first time with right-click → Open.
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

[Unreleased]: https://github.com/AlexandruMindra/TGK/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/AlexandruMindra/TGK/compare/v0.2.4...v0.3.0
[0.2.4]: https://github.com/AlexandruMindra/TGK/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/AlexandruMindra/TGK/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/AlexandruMindra/TGK/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/AlexandruMindra/TGK/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/AlexandruMindra/TGK/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/AlexandruMindra/TGK/releases/tag/v0.1.0
