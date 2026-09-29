# tgk-server

A small sync server for TGK. It is one executable with one SQLite file. It handles accounts, sign-in with a password
plus an authenticator app (TOTP), signed-in devices, and sync of the vault. The vault is end-to-end encrypted: the
server only stores ciphertext and never sees passwords or vault keys.

## Build and publish

```bash
dotnet run --project src/TGK.Server -- serve --db ./data/tgk.db      # development
scripts/publish-server.sh                  # self-contained single file for linux-x64 and win-x64 -> dist/server/<rid>/
scripts/publish-server.sh linux-arm64      # or any other runtime identifier
```

The published file (about 50 MB) needs no .NET installation. On Linux it uses the system ICU library (`libicu`),
which most distributions install by default.

## Run

```bash
tgk-server                                            # = serve on http://127.0.0.1:5080 with ./data/tgk.db
tgk-server serve --urls https://0.0.0.0:5443 --cert server.pfx --cert-password '...'
tgk-server serve --urls http://127.0.0.1:5080 --trust-proxy                       # behind Caddy or nginx
```

| Option | Default | |
|---|---|---|
| `--db <path>` | `./data/tgk.db` (env `TGK_DB`) | Created with mode 0600 if it does not exist. Used by every command. |
| `--urls <urls>` | `http://127.0.0.1:5080` (env `TGK_URLS`) | Separate several URLs with `;`. |
| `--cert <pfx>` `--cert-password <pw>` | | Serve HTTPS directly. Use `https://` URLs. |
| `--trust-proxy` | off | Honour `X-Forwarded-For` and `X-Forwarded-Proto` from a proxy on loopback. Client IPs are then correct in logs, rate limits and the session list. |
| `--rate-limit <n>` | 20 | Requests per minute per client IP to prelogin, login, register and password change combined. |

Clients refuse plain `http://` except on loopback. Expose the server only over HTTPS, either with `--cert` or with a
reverse proxy. Logs go to the console on one line each. They never contain tokens, keys, TOTP secrets or codes, or
vault data.

## Admin commands

Admin commands work directly on the database file, and a running server sees the changes straight away. Pass the
same `--db` as the server, or set `TGK_DB`.

```
tgk-server users                          List accounts (status, authenticator, items, sessions, last seen)
tgk-server user create <name>             Invite a user: prints a one-time code (valid 7 days) for "Create an account" in the app
tgk-server user show <name>               Account details and signed-in devices
tgk-server user disable <name>            Block sign-in and sign out every device
tgk-server user enable <name>
tgk-server user delete <name> [--yes]     Delete the account and all of its synced data (or cancel a pending invite)
tgk-server user reset-totp <name>         Lost phone: the next sign-in asks the user to set up a new authenticator
tgk-server user revoke-sessions <name>    Sign out every device
tgk-server registration on|off|status     Self-registration from clients (on for a new database)
```

Self-registration is on for a new database. On a server anyone can reach, run `tgk-server registration off` and
invite each user with `user create <name>`. The user then chooses "Create an account" in the app with that username
and the printed invite code, which works once, also while registration is off. The password, the vault key and the
authenticator are set up on the user's device, so the admin never knows them.

Each account may store at most 50,000 items (deleted ones included) and 64 MB of encrypted data. A push beyond that
is refused (`payload_too_large`); edits that shrink a full vault are still accepted.

## systemd

```ini
# /etc/systemd/system/tgk-server.service
[Unit]
Description=TGK sync server
After=network.target

[Service]
User=tgk
ExecStart=/opt/tgk/tgk-server serve --urls http://127.0.0.1:5080 --db /var/lib/tgk/tgk.db --trust-proxy
Restart=on-failure
StateDirectory=tgk
# The single-file build unpacks its native libraries here (the service user has no home directory).
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/tgk/.bundle
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin tgk
sudo systemctl enable --now tgk-server
sudo -u tgk env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/tgk/.bundle /opt/tgk/tgk-server --db /var/lib/tgk/tgk.db users
```

## Caddy reverse proxy

Caddy obtains and renews the TLS certificate automatically:

```
# /etc/caddy/Caddyfile
tgk.example.com {
    reverse_proxy 127.0.0.1:5080
}
```

Run tgk-server with `--trust-proxy` so it takes the client IP from Caddy's `X-Forwarded-For`. It trusts those
headers only when the proxy connects from loopback. nginx works the same way: `proxy_pass http://127.0.0.1:5080;`
together with `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;` and
`proxy_set_header X-Forwarded-Proto $scheme;`.

## Backup

The database is in WAL mode, so do not copy the file while the server is running. Take a consistent snapshot
instead:

```bash
sqlite3 /var/lib/tgk/tgk.db ".backup '/backups/tgk-$(date +%F).db'"
```

To restore, stop the server, put the backup at the `--db` path, delete any `tgk.db-wal` and `tgk.db-shm` files
next to it, and start the server again.

## Client versions

Update the client on every device before using per-host settings (jump hosts, tunnels, keep-alive, the connection
defaults in Settings, and so on). Clients from before those settings ignore the connection defaults, and whenever
they save a host or a group (even when a group is collapsed in the sidebar) they drop its settings and tunnels.
Clients that have per-host settings keep the fields they don't know when they save, so settings added in later
versions survive them.

## Security notes

- **End-to-end encryption.** The vault key is wrapped with a key derived from the user's password on the client.
  The server stores only a verifier of a derived auth key, the wrapped vault key, and encrypted items. A forgotten
  password therefore cannot be recovered: neither the admin nor the server can decrypt the vault. The admin deletes
  the account (`user delete`), and the user registers again (or is invited again with `user create`) with an
  empty vault.
- **TOTP secrets are stored in plaintext** in the database, because the server has to check the codes. Anyone who
  has the database file can generate codes. Protect the file and its backups: it is created with mode 0600, and the
  data directory should belong to the service account only. Even with the file, an attacker still needs the
  password to sign in or to decrypt anything.
- **Sessions.** Each session token is 32 random bytes, and the server stores only its SHA-256 hash. A session expires
  after 30 days without use. Users can sign out other devices from any client. Admins can do it with
  `user revoke-sessions`, and `user disable` does it too.
- **Brute force.** Auth endpoints are rate-limited per IP. After 5 consecutive failed sign-ins, a username is
  locked for 5 minutes. Unknown usernames are locked the same way, so a lockout reveals nothing. The lockout
  counters are kept in memory and reset when the server restarts.
- **What the server can see.** Usernames, item ids, sizes and timestamps, device names and IP addresses. Item ids
  are random, or for known host keys an HMAC under a key derived from the vault key, so they do not reveal host
  names. When a host was last connected to stays on each device and is not synced.
- **No user enumeration.** Prelogin returns a stable fake salt for unknown users. Login returns the same error for
  an unknown user and for a wrong password.
