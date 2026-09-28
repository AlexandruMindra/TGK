# Development testing

All commands run from the repository root with the .NET 10 SDK on `PATH`.

## Unit tests

```bash
dotnet test tests/TGK.Terminal.Tests
dotnet test tests/TGK.Core.Tests          # end-to-end tests are skipped unless configured (below)
```

## End-to-end SSH tests

`tests/TGK.Core.Tests/SshEndToEndTests.cs` (trait `Category=E2E`) opens real sessions and renders them through the
terminal emulator. It needs a throwaway SSH server on a loopback port that gives each session a `bash` login shell
in a PTY, honours `pty-req`/`window-change`, and accepts one test user by password and by an authorized key.
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
dotnet test tests/TGK.Core.Tests --filter Category=E2E
```

Never point these variables at a real server or account.

## GUI checks

The client has developer flags for driving UI states without a mouse (harmless unless passed):

| Flag | Effect |
|---|---|
| `--dev-login` | Sign in to the mock vault as `demo`. |
| `--scene=<name>` | Open a UI state: `login`, `main`, `host-editor`, `identities`, `settings`, `hostkey`, `hostkey-changed`, `password-prompt`, `menu`. |
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
