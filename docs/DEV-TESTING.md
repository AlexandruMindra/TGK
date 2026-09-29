# Development testing

All commands run from the repository root with the .NET 10 SDK on `PATH`.

## Unit tests

```bash
scripts/test.sh                           # fastest: builds, then runs all test executables in parallel (~10 s)
scripts/test.sh --no-build -class "*VaultTests"   # xunit filters are passed through
dotnet test TGK.slnx                      # everything; the SSH end-to-end tests are skipped unless configured (below)
dotnet test tests/TGK.Server.Tests --filter FullyQualifiedName~ClientServerE2E   # real client against the in-process server
```

`ClientServerE2ETests` drives the client's `RemoteVaultService` against the server in-process (two devices:
register, sync, delete, revoke, password change) and checks that the SQLite file holds no plaintext.

## Local sync server

For client development against a real server (see [SERVER.md](SERVER.md) for the server itself):

```bash
dotnet build src/TGK.Server
dotnet src/TGK.Server/bin/Debug/net10.0/tgk-server.dll serve --urls http://127.0.0.1:5080 --db /tmp/tgk-dev/tgk.db
```

In the client, use `http://127.0.0.1:5080` as the server URL (plain `http://` is only accepted for loopback hosts)
and "Create an account"; the QR code works with Google Authenticator or any TOTP app, and the setup key can be
copied instead. Useful CLI commands against the same database (they take effect immediately):
`user reset-totp <name>` (the next sign-in shows the authenticator setup step), `user revoke-sessions <name>` (the
client returns to the login screen with a notice on its next sync), `registration off` plus `user create <name>`
(the invite code goes into "Create an account").

"Keep me signed in" stores the session and an encrypted vault cache in the config directory (`XDG_CONFIG_HOME`), so
the next start opens the main window directly; use a throwaway `XDG_CONFIG_HOME` to start clean. `mock://localhost`
still selects the built-in mock vault (any username and password, no authenticator code).

## End-to-end SSH tests

`tests/TGK.Core.Tests/SshEndToEndTests.cs` (trait `Category=E2E`) opens real sessions and renders them through the
terminal emulator. It needs a throwaway SSH server on a loopback port that gives each session a `bash` login shell
in a PTY, honours `pty-req`/`window-change`, and accepts one test user by password and by an authorized key. The jump
host and tunnel tests also need it to allow port forwarding (`direct-tcpip` and `tcpip-forward`; jump tests reach the
server through itself, also as `localhost`), and the environment test passes only when it accepts `env` requests
(it is skipped otherwise).
A small asyncssh script is enough (or any disposable OpenSSH server in a container or VM):

```bash
python3 -m venv .venv-ssh && .venv-ssh/bin/pip install asyncssh
ssh-keygen -q -t ed25519 -N "" -f <keydir>/client_key     # authorize client_key.pub on the test server
.venv-ssh/bin/python <path-to>/server.py                    # listens on 127.0.0.1:2222
```

Run the tests against it:

```bash
export TGK_E2E_SSH='<user>:<password>@127.0.0.1:2222'
export TGK_E2E_SSH_KEY=<keydir>/client_key                   # optional: enables the private-key test
export TGK_E2E_SSH_LEGACY='<user>:<password>@127.0.0.1:2223'  # optional: a server offering only legacy key exchange
                                                             # (e.g. diffie-hellman-group14-sha1) for the "Legacy algorithms" test
dotnet test tests/TGK.Core.Tests --filter Category=E2E
```

Never point these variables at a real server or account.

## GUI checks

The client has developer flags for driving UI states without a mouse (harmless unless passed):

| Flag | Effect |
|---|---|
| `--dev-login` | Sign in to the mock vault as `demo` (never restores a kept session). |
| `--scene=<name>` | Open a UI state. Login screen: `login`, `login-totp` (authenticator reset), `register`, `register-totp` (these never restore a kept session). Signed in, on a kept session if one is restored, else the mock vault: `main`, `host-editor` (also `host-editor-connection`, `host-editor-tunnels`, `host-editor-appearance`: the host editor on that tab), `group-settings`, `connection-defaults` (Settings on the synced connection defaults), `identities`, `settings`, `hostkey`, `hostkey-changed`, `password-prompt`, `menu`, `devices`, `change-password`. The mock vault's sample hosts include a group-level jump host (Production → bastion), a tunnel (db-primary) and a color scheme (homelab). |
| `--dev-connect=<user>[:<password>]@<host>[:<port>]` | After sign-in, open a session tab to that address (no password: the prompt appears). |
| `--dev-send=<text>` | Typed into the `--dev-connect` session once connected; `\r`, `\n`, `\t`, `\e` and `\\` are unescaped. |
| `--window=<W>x<H>` | Initial window size. |
| `--fps` | Blossom's frame-time overlay. |

Screenshots on a private X server (never the real display), with a throwaway config directory:

```bash
dotnet build src/TGK.Client
Xvfb :99 -screen 0 1280x800x24 &
cd src/TGK.Client/bin/Debug/net10.0
DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 XDG_CONFIG_HOME=/tmp/tgk-xdg \
  ~/.dotnet/dotnet TGK.dll --dev-connect='<user>:<password>@127.0.0.1:2222' --dev-send='ls --color=always -la\r' &
sleep 6 && DISPLAY=:99 scrot -o /tmp/tgk-session.png
```

The first connection to a host shows the host-key prompt; `DISPLAY=:99 xte 'key Return'` trusts it (the key is then
stored in the mock vault under `XDG_CONFIG_HOME`). The prompt accepts Enter only after the keyboard has been quiet
for a second since it opened, so wait a moment before sending it. Kill the app and Xvfb when done.
