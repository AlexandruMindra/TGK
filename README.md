# TGK

A cross-platform SSH session and credential manager with browser-style tabs. Keep your hosts, keys and connection
settings in an end-to-end encrypted vault — synced across devices through a lightweight self-hosted server, or kept
entirely on one machine.

![Home](docs/images/home.png)

## Features

**Sessions**
- Tabs like a browser: every tab is an SSH terminal; a new tab offers quick connect (`user@host:port`) and your saved hosts.
  Any number of tabs: the tab strip scrolls (mouse wheel, touchpad, the ‹ › arrows) and lists them all.
- Split view: show several sessions at once — 2 or 3 side by side, stacked, 1 large + 2, grids of 4 or 6 (the layout
  button in the tab strip), or split any pane right / down (`Ctrl+Shift+E` / `Ctrl+Shift+O`). Drag the dividers to
  resize, `Alt+arrows` to move between panes, `Ctrl+Shift+X` to maximize one; each pane stays a tab of its own.
- Reopen my tabs (Settings → Startup, off by default): your tabs and split views are saved in the vault when you close
  TGK, sign out or lock it (and as you work), and reopened on whichever device you sign in next. Only which hosts were
  open is saved, never passwords; a session connects when you open its tab.
- Updates: a while after start (never during it) and about twice a day, TGK asks GitHub for a newer release
  (nightly builds also look at the latest nightly). A newer one only adds a small note to the status bar: "Update now"
  downloads it in the background, checks it against GitHub's checksum and installs it when you restart (or close) TGK;
  "Download from GitHub", "Skip this version" and "Remind me later" are there too. Turn it off in Settings → Startup;
  "Check for updates" in the account menu asks right away. Builds run from source only offer the download page.
- Built-in xterm-compatible terminal: 256 colors and truecolor, alternate screen (vim, htop, mc, tmux), scrollback,
  mouse reporting, bracketed paste, wide characters, selection with copy/paste.
- Auth with private keys (OpenSSH/PEM, with passphrase), password or keyboard-interactive; host key verification
  on first connect and on change.

**Hosts**
- Groups, search, tag colors, identities (username + password or private key) shared between hosts.
- Per-host options with inheritance (built-in default ← global ← group ← host):
  - Jump hosts (chains up to 4 hops), keep-alive, connect timeout, automatic reconnect.
  - Local, remote and dynamic (SOCKS) port forwarding, started with the session.
  - Startup command, environment variables, terminal type.
  - Font size, color scheme (TGK Dark, Solarized, Dracula, Nord, Gruvbox, One Dark), opt-in legacy algorithms for old devices.

**Vault**
- **Server mode:** sign up from the app, Google Authenticator (TOTP) required, stay signed in per device, see and
  sign out your other devices, change password. Changes sync in the background and work offline.
- **Local mode:** no server at all — a local SQLite vault protected by a master password.
- Move between modes at any time (upload a local vault to an account, save an offline copy of an account),
  plus encrypted `.tgkbackup` export/import.

| Terminal | Per-host options | Local vault |
|---|---|---|
| ![Terminal](docs/images/terminal.png) | ![Host options](docs/images/host-options.png) | ![Local vault](docs/images/local-vault.png) |

## Security model

- The vault is **end-to-end encrypted**. Your password never leaves the device: PBKDF2-SHA256 (600k iterations)
  derives an authentication key and a key-encryption key (HKDF); a random vault key, wrapped by the latter, encrypts
  every host, group, identity and known host separately with AES-256-GCM, bound to its item id.
- The server stores only opaque blobs, a verifier of the authentication key, the TOTP secret and session tokens (hashed).
- **A forgotten password (or master password) cannot be recovered** — nobody, including the server admin, can decrypt the vault.
- On a device where you choose "stay signed in" / "keep unlocked", the session token and vault key are stored in the
  OS keyring (DPAPI on Windows, libsecret via `secret-tool` on Linux, otherwise a file readable only by you).

## Getting started

Ready-to-run Linux and Windows builds (no .NET needed): the [latest release](https://github.com/AlexandruMindra/TGK/releases/latest),
or the [nightly](https://github.com/AlexandruMindra/TGK/releases/tag/nightly) build of `main` (versioning rules: [docs/RELEASING.md](docs/RELEASING.md)). To build from source you need the
[.NET 10 SDK](https://dotnet.microsoft.com/download). Tested mainly on Linux; Windows is supported but less tested.

```bash
git clone --recurse-submodules https://github.com/AlexandruMindra/TGK.git
cd TGK
dotnet run --project src/TGK.Client
```

Pick **This device only** on the sign-in screen to use TGK without a server.

### Sync server

```bash
dotnet run --project src/TGK.Server -- serve --urls http://127.0.0.1:5080
```

Then choose **Server account → Create an account** in the app. For anything beyond localhost, put the server behind
HTTPS (the client refuses plain `http://` except on loopback). Admin commands:

```bash
tgk-server users                        # list accounts and pending invites
tgk-server user disable|enable|delete <name>
tgk-server user reset-totp <name>       # lost phone: the user enrolls a new authenticator at next sign-in
tgk-server user revoke-sessions <name>
tgk-server user create <name>           # reserve a name and print a one-time invite code
tgk-server registration on|off|status
```

Publishing, systemd, Caddy and backups: [docs/SERVER.md](docs/SERVER.md).

## Development

```text
src/TGK.Terminal   VT/xterm emulator (no UI dependencies)
src/TGK.Protocol   shared DTOs, vault crypto, TOTP
src/TGK.Core       models, vault services (server, local), SSH sessions (SSH.NET)
src/TGK.Client     desktop app (Blossom: Silk.NET + SkiaSharp)
src/TGK.Server     sync server (ASP.NET Core minimal API + SQLite)
tests/             xunit test projects
```

```bash
scripts/test.sh    # builds and runs all test projects in parallel (~10 s)
```

End-to-end SSH tests against a local test server and GUI checks: [docs/DEV-TESTING.md](docs/DEV-TESTING.md).

## License

[MIT](LICENSE). Third-party components: [Blossom](https://github.com/cretucosmin3/Blossom) (MIT),
[SSH.NET](https://github.com/sshnet/SSH.NET) (MIT), SkiaSharp and Silk.NET (MIT), QRCoder (MIT),
DejaVu Sans Mono ([license](assets/fonts/DejaVu-LICENSE.txt)).
