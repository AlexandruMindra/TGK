# tgk-server

A small sync server for TGK. It is one executable with one SQLite file. It handles accounts, sign-in with a password
plus an authenticator app (TOTP), signed-in devices, and sync of the vault. The vault is end-to-end encrypted: the
server only stores ciphertext and never sees passwords or vault keys.

## Install on Linux, with automatic updates (LXC, VM, server)

`tgk-server-manage` installs tgk-server on any systemd-based Linux (Debian, Ubuntu, Fedora…; x86_64 or ARM64), in
a Proxmox LXC container too, and then looks after it:

- **Updates** every night from the stable releases (never the nightly), within the same MAJOR version. Each update
  stops the server, snapshots the database, starts the new version and checks that it answers; if it does not, it
  goes back to the previous version and that snapshot, and skips that version from then on. A new MAJOR version is
  only announced (`tgk-server-manage status`); install it with `tgk-server-manage update --force` after reading its
  release notes. The downloads are checked against the release's SHA-256 sums.
- **Backups** every night to `/var/backups/tgk/daily` (the last 14 by default), made while the server runs.
- Self-registration is **off** on a new install: invite each user with `tgk-server user create <name>`.

In the container, as root:

```bash
curl -fsSLO https://github.com/AlexandruMindra/TGK/releases/latest/download/tgk-server-manage.sh
bash tgk-server-manage.sh install --known-proxy 192.168.1.2     # the address of the reverse proxy (HAProxy, nginx…)
tgk-server user create alex                                     # prints an invite code for "Create an account"
```

| Option of `install` | |
|---|---|
| `--known-proxy <ip\|cidr>` | A TLS reverse proxy on another machine. tgk-server then listens on `0.0.0.0:5080` and takes the client's address from the proxy's `X-Forwarded-For`. Repeat for several. |
| `--listen <host:port>` | Where it listens (default `127.0.0.1:5080`, or `0.0.0.0:5080` with `--known-proxy`). |
| `--keep-backups <n>` | Daily backups kept (14). |
| `--no-auto-update` | Update only with `tgk-server-manage update`. |
| `--open-registration` | Leave self-registration on. |

Afterwards:

```
tgk-server-manage status        Installed and latest version, service, timers, last backup
tgk-server-manage update        Install the latest stable release now (--force: also a new MAJOR, or a skipped one)
tgk-server-manage backup        Back up now
tgk-server-manage uninstall     Remove the service and programs (keeps the database, backups and settings)
tgk-server <admin command>      users, user create <name>, registration off, ... (as root; runs as the service user)
```

Settings are in `/etc/tgk/server.env` (`systemctl restart tgk-server` after a change); running `install` again with
other options updates them. Files: the programs in `/opt/tgk` (the last few versions, for rollbacks), the database in
`/var/lib/tgk`, backups in `/var/backups/tgk` (`daily/`, and `updates/` for the snapshots taken before updates). The
service runs as the `tgk` system user. Logs: `journalctl -u tgk-server`, and `journalctl -u tgk-server-update` for
the updates.

In an unprivileged LXC without the "nesting" feature, systemd can't set up the service's sandbox; the installer then
runs the service without it and says so. To keep the sandbox, turn on nesting (Proxmox: the container's Options →
Features) and delete `/etc/systemd/system/tgk-server.service.d/container.conf`.

### Behind HAProxy (TLS on a subdomain)

Clients accept only `https://` with a certificate their system trusts, so TLS ends at the proxy. With the proxy at
192.168.1.2 and the container at 192.168.1.20:

```
frontend https
    bind :443 ssl crt /etc/haproxy/certs/
    mode http
    option forwardfor                          # sets X-Forwarded-For
    http-request set-header X-Forwarded-Proto https
    use_backend tgk if { req.hdr(host) -i tgk.example.com }

backend tgk
    mode http
    option httpchk GET /api/info
    timeout server 60s
    server tgk 192.168.1.20:5080 check
```

and `install --known-proxy 192.168.1.2` in the container. The app's server address is then
`https://tgk.example.com`. Between the proxy and the container the traffic is plain HTTP: keep it on your own network,
and if others share that network, let only the proxy reach port 5080 (a firewall rule in the container or on the
host). The vault stays end-to-end encrypted either way, but session tokens cross that link readable.

## Build and publish

```bash
dotnet run --project src/TGK.Server -- serve --db ./data/tgk.db      # development
scripts/publish-server.sh                  # self-contained single file for linux-x64 and win-x64 -> dist/server/<rid>/
scripts/publish-server.sh linux-arm64      # or any other runtime identifier
```

The published file (about 50 MB) needs no .NET installation, nor any library beyond glibc (no ICU: the server
runs culture-invariant). Releases carry it for Linux as `tgk-server-linux-x64` and `tgk-server-linux-arm64`.

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
| `--known-proxy <ip\|cidr>` | (env `TGK_KNOWN_PROXIES`) | Honour them also from a proxy on another machine (an address such as `192.168.1.2` or a network such as `10.0.0.0/24`; repeat or separate with commas). Without it, behind such a proxy every client has the proxy's address, and they share one rate limit. |
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
tgk-server backup <file>                  Consistent copy of the database, also while the server runs
tgk-server --version
```

Self-registration is on for a new database. On a server anyone can reach, run `tgk-server registration off` and
invite each user with `user create <name>`. The user then chooses "Create an account" in the app with that username
and the printed invite code, which works once, also while registration is off. The password, the vault key and the
authenticator are set up on the user's device, so the admin never knows them.

Each account may store at most 50,000 items (deleted ones included) and 64 MB of encrypted data. A push beyond that
is refused (`payload_too_large`); edits that shrink a full vault are still accepted.

## systemd (by hand)

`tgk-server-manage` (above) sets this up by itself. To do it by hand:

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

`tgk-server-manage` backs up every night by itself. Otherwise: the database is in WAL mode, so do not copy the file
while the server is running. Take a consistent snapshot instead (mode 0600, checked for integrity):

```bash
tgk-server --db /var/lib/tgk/tgk.db backup /backups/tgk-$(date +%F).db
```

To restore, stop the server, put the backup at the `--db` path, delete any `tgk.db-wal` and `tgk.db-shm` files
next to it, and start the server again.

## Client versions

Update the client on every device before using per-host settings (jump hosts, tunnels, keep-alive, the connection
defaults in Settings, and so on). Clients from before those settings ignore the connection defaults, and whenever
they save a host or a group (even when a group is collapsed in the sidebar) they drop its settings and tunnels.
Clients that have per-host settings keep the fields they don't know when they save, so settings added in later
versions survive them. "Reopen my tabs" (Settings → Startup) is stored as an item of its own that older clients skip:
they neither reopen nor change the saved tabs.

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
