#!/usr/bin/env bash
# tgk-server-manage: installs tgk-server on a Linux machine or container (LXC, VM) with systemd, then keeps it up to
# date from TGK's stable releases and backs it up every day. Run as root. Documentation: docs/SERVER.md.
#
#   tgk-server-manage install [--known-proxy <ip|cidr>] [--listen <host:port>] [--no-auto-update]
#                             [--keep-backups <n>] [--open-registration]
#   tgk-server-manage update [--force]     Install the latest stable release if it is newer (the daily timer runs this)
#   tgk-server-manage backup               Back up the database now (the daily timer runs this)
#   tgk-server-manage status               Version, service, timers, last backups
#   tgk-server-manage uninstall            Remove the service, timers and programs; keeps the data and the backups
#
# An update stops the server, snapshots the database, switches to the new version and checks that it answers. If it
# does not, the update is rolled back (previous version and the snapshot) and that version is skipped from then on.
# Updates never cross a MAJOR version on their own (that may need a step by hand: see the release notes).
set -euo pipefail

REPO="AlexandruMindra/TGK"
# Where releases are downloaded from. Tests point this at a local copy laid out the same way:
# <base>/latest/download/<file> and <base>/download/v<version>/<file>.
RELEASES="${TGK_RELEASE_BASE:-https://github.com/$REPO/releases}"

PREFIX=/opt/tgk                  # releases/<version>/tgk-server, current -> releases/<version>
DATA=/var/lib/tgk                # tgk.db, .bundle (the single-file executable unpacks its native libraries here)
BACKUPS=/var/backups/tgk         # daily/ (kept: TGK_KEEP_BACKUPS), updates/ (snapshots taken before updates)
CONFIG_DIR=/etc/tgk
CONFIG="$CONFIG_DIR/server.env"
SELF=/usr/local/sbin/tgk-server-manage
WRAPPER=/usr/local/bin/tgk-server
UNITS=/etc/systemd/system
SERVICE_USER=tgk
KEEP_RELEASES=3
KEEP_UPDATE_SNAPSHOTS=5

log() { printf '%s\n' "$*"; }
die() { printf 'tgk-server-manage: %s\n' "$*" >&2; exit 1; }

need_root() { [ "$(id -u)" -eq 0 ] || die "run this as root (sudo)."; }

rid() {
  case "$(uname -m)" in
    x86_64 | amd64) echo linux-x64 ;;
    aarch64 | arm64) echo linux-arm64 ;;
    *) die "unsupported CPU architecture $(uname -m) (supported: x86_64, aarch64)." ;;
  esac
}

fetch() { # url dest
  curl -fsSL --retry 3 --retry-delay 5 --connect-timeout 20 -o "$2" "$1"
}

# The version (X.Y.Z) of the latest stable release (GitHub's "latest" never points at the nightly).
latest_version() {
  local v
  v=$(curl -fsSL --retry 3 --retry-delay 5 --connect-timeout 20 "$RELEASES/latest/download/tgk-server.version" | tr -d '[:space:]')
  [[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] || die "could not read the latest release's version (got '$v')."
  echo "$v"
}

installed_version() { cat "$PREFIX/VERSION" 2>/dev/null || true; }

major() { echo "${1%%.*}"; }

# Newer by MAJOR.MINOR.PATCH (pre-release suffixes are not compared: only stable releases are installed).
is_newer() { # candidate current
  local a b
  a=$(echo "${1%%[-+]*}" | awk -F. '{ printf "%05d%05d%05d", $1, $2, $3 }')
  b=$(echo "${2%%[-+]*}" | awk -F. '{ printf "%05d%05d%05d", $1, $2, $3 }')
  [[ "$a" > "$b" ]]
}

load_config() {
  TGK_URLS="http://127.0.0.1:5080"
  TGK_DB="$DATA/tgk.db"
  TGK_KEEP_BACKUPS=14
  TGK_AUTO_UPDATE=1
  if [ -f "$CONFIG" ]; then
    # shellcheck disable=SC1090
    . "$CONFIG"
  fi
}

# The address the health check calls: the first listen URL, with a wildcard host replaced by loopback.
health_url() {
  local url="${TGK_URLS%%;*}"
  url=$(echo "$url" | sed -E 's#//(0\.0\.0\.0|\*|\+|\[::\])(:|/|$)#//127.0.0.1\2#')
  echo "${url%/}/api/info"
}

# Runs the server executable as the service user (admin commands, backups, version checks).
as_service() { # executable args...
  local exe="$1"
  shift
  runuser -u "$SERVICE_USER" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR="$DATA/.bundle" TGK_DB="$TGK_DB" "$exe" "$@"
}

# Downloads tgk-server and this script of <version> into releases/<version> and checks them against the release's
# SHA-256 sums.
download_release() { # version
  local version="$1" dir="$PREFIX/releases/$1" tmp="$PREFIX/.download" rid
  rid=$(rid)
  rm -rf "$tmp"
  mkdir -p "$tmp"
  local base="$RELEASES/download/v$version"
  fetch "$base/tgk-server-SHA256SUMS" "$tmp/SHA256SUMS" || die "could not download the checksums of $version."
  fetch "$base/tgk-server-$rid" "$tmp/tgk-server-$rid" || die "could not download tgk-server-$rid $version."
  fetch "$base/tgk-server-manage.sh" "$tmp/tgk-server-manage.sh" || die "could not download tgk-server-manage.sh $version."
  # Both files must be listed, and match.
  grep -E " \*?(tgk-server-$rid|tgk-server-manage\.sh)\$" "$tmp/SHA256SUMS" > "$tmp/expected" || true
  if [ "$(wc -l < "$tmp/expected")" -ne 2 ] || ! (cd "$tmp" && sha256sum -c --status expected); then
    rm -rf "$tmp"
    die "the downloaded files of $version do not match their SHA-256 checksums; nothing was changed."
  fi
  rm -rf "$dir"
  mkdir -p "$dir"
  install -m 0755 "$tmp/tgk-server-$rid" "$dir/tgk-server"
  install -m 0755 "$tmp/tgk-server-manage.sh" "$dir/tgk-server-manage"
  rm -rf "$tmp"
  # It must start and be the version it claims.
  local reported
  reported=$(as_service "$dir/tgk-server" --version 2>&1) || die "tgk-server $version does not start on this system: $reported"
  [ "${reported%%[-+]*}" = "${version%%[-+]*}" ] || die "the downloaded tgk-server says it is '$reported', expected $version."
}

wait_healthy() { # version
  local expected="${1%%[-+]*}" url body
  url=$(health_url)
  for _ in $(seq 1 30); do
    if body=$(curl -fsS --max-time 3 "$url" 2>/dev/null) && [[ "$body" == *"\"version\":\"$expected\""* ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

snapshot() { # label -> prints the snapshot directory
  local dir
  dir="$BACKUPS/updates/$(date -u +%Y%m%dT%H%M%SZ)-$1"
  mkdir -p "$dir"
  chmod 700 "$BACKUPS/updates"
  local f
  for f in "$TGK_DB" "$TGK_DB-wal" "$TGK_DB-shm"; do
    if [ -f "$f" ]; then cp -a "$f" "$dir/"; fi
  done
  echo "$dir"
}

restore_snapshot() { # dir
  local f
  rm -f "$TGK_DB" "$TGK_DB-wal" "$TGK_DB-shm"
  for f in "$1"/*; do
    if [ -f "$f" ]; then cp -a "$f" "$(dirname "$TGK_DB")/"; fi
  done
}

switch_to() { # version
  ln -sfn "releases/$1" "$PREFIX/current"
  echo "$1" > "$PREFIX/VERSION"
}

prune() {
  local current previous
  current=$(installed_version)
  previous=$(cat "$PREFIX/PREVIOUS" 2>/dev/null || true)
  # Old releases (the current and the previous one stay).
  { ls -1t "$PREFIX/releases" 2>/dev/null || true; } | { grep -vxF -e "$current" -e "${previous:-$current}" || true; } \
    | tail -n +$((KEEP_RELEASES)) | while read -r old; do rm -rf "${PREFIX:?}/releases/$old"; done
  { ls -1dt "$BACKUPS"/updates/*/ 2>/dev/null || true; } | tail -n +$((KEEP_UPDATE_SNAPSHOTS + 1)) \
    | while read -r old; do rm -rf "$old"; done
}

cmd_update() {
  local force=0 auto=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --force) force=1 ;;
      --auto) auto=1 ;;
      *) die "unknown option '$1' for update." ;;
    esac
    shift
  done
  need_root
  load_config
  [ -f "$PREFIX/VERSION" ] || die "tgk-server is not installed here; run: tgk-server-manage install"
  exec 9> /run/tgk-server-manage.lock
  flock -n 9 || die "another tgk-server-manage is running."

  local current target
  current=$(installed_version)
  target=$(latest_version)
  if ! is_newer "$target" "$current" && [ "$force" -eq 0 ]; then
    log "tgk-server $current is up to date."
    return 0
  fi
  if [ "$(major "$target")" != "$(major "$current")" ] && [ "$force" -eq 0 ]; then
    log "tgk-server $target is out, a new major version: not installed automatically. Read its release notes, then run: tgk-server-manage update --force"
    return 0
  fi
  if [ "$auto" -eq 1 ] && [ "$(cat "$PREFIX/FAILED" 2>/dev/null || true)" = "$target" ]; then
    log "tgk-server $target failed to start here before and was rolled back; skipping it. Fix, then: tgk-server-manage update --force"
    return 0
  fi

  log "Updating tgk-server $current -> $target"
  download_release "$target"
  systemctl stop tgk-server
  local snap
  snap=$(snapshot "before-$target")
  echo "$current" > "$PREFIX/PREVIOUS"
  switch_to "$target"
  systemctl start tgk-server || true
  if wait_healthy "$target"; then
    rm -f "$PREFIX/FAILED"
    install -m 0755 "$PREFIX/releases/$target/tgk-server-manage" "$SELF.new" && mv -f "$SELF.new" "$SELF"
    prune
    log "tgk-server $target is running (database snapshot from before the update: $snap)."
    return 0
  fi

  log "tgk-server $target did not answer at $(health_url); rolling back to $current." >&2
  journalctl -u tgk-server -n 30 --no-pager >&2 || true
  systemctl stop tgk-server || true
  restore_snapshot "$snap"
  switch_to "$current"
  echo "$target" > "$PREFIX/FAILED"
  systemctl reset-failed tgk-server 2> /dev/null || true # the failed restarts must not hit the start limit
  systemctl start tgk-server
  wait_healthy "$current" || die "the rollback to $current did not start either; see: journalctl -u tgk-server"
  die "update to $target failed and was rolled back; $current is running again."
}

# daily/ is written by the service user (the online backup runs as it), updates/ by root; the parent only lets the
# service user through.
backup_dirs() {
  mkdir -p "$BACKUPS/daily" "$BACKUPS/updates"
  chown "root:$SERVICE_USER" "$BACKUPS"
  chmod 750 "$BACKUPS"
  chown "$SERVICE_USER:" "$BACKUPS/daily"
  chmod 700 "$BACKUPS/daily" "$BACKUPS/updates"
}

cmd_backup() {
  need_root
  load_config
  [ -f "$TGK_DB" ] || { log "No database yet ($TGK_DB): nothing to back up."; return 0; }
  backup_dirs
  local file
  file="$BACKUPS/daily/tgk-$(date +%Y-%m-%d).db"
  as_service "$PREFIX/current/tgk-server" backup "$file"
  { ls -1t "$BACKUPS"/daily/tgk-*.db 2>/dev/null || true; } | tail -n +$((TGK_KEEP_BACKUPS + 1)) \
    | while read -r old; do rm -f "$old"; done
}

set_config() { # key value
  if grep -q "^$1=" "$CONFIG"; then
    sed -i "s#^$1=.*#$1=$2#" "$CONFIG"
  else
    echo "$1=$2" >> "$CONFIG"
  fi
}

write_units() {
  cat > "$UNITS/tgk-server.service" <<EOF
# Installed by tgk-server-manage; settings: $CONFIG
[Unit]
Description=TGK sync server
After=network-online.target
Wants=network-online.target

[Service]
User=$SERVICE_USER
EnvironmentFile=$CONFIG
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$DATA/.bundle
ExecStart=$PREFIX/current/tgk-server serve --trust-proxy \$TGK_EXTRA_ARGS
Restart=on-failure
RestartSec=5
StateDirectory=tgk
StateDirectoryMode=0700
WorkingDirectory=$DATA
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
  cat > "$UNITS/tgk-server-backup.service" <<EOF
[Unit]
Description=Back up the TGK sync server's database

[Service]
Type=oneshot
ExecStart=$SELF backup
EOF
  cat > "$UNITS/tgk-server-backup.timer" <<EOF
[Unit]
Description=Daily backup of the TGK sync server

[Timer]
OnCalendar=*-*-* 03:00
RandomizedDelaySec=30min
Persistent=true

[Install]
WantedBy=timers.target
EOF
  cat > "$UNITS/tgk-server-update.service" <<EOF
[Unit]
Description=Update the TGK sync server to the latest stable release
After=network-online.target tgk-server-backup.service
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=$SELF update --auto
EOF
  cat > "$UNITS/tgk-server-update.timer" <<EOF
[Unit]
Description=Daily update check of the TGK sync server

[Timer]
OnCalendar=*-*-* 04:00
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
EOF
  # Admin commands as the service user, on the right database: tgk-server users, tgk-server user create <name>, ...
  cat > "$WRAPPER" <<EOF
#!/bin/sh
# Installed by tgk-server-manage: runs tgk-server admin commands as $SERVICE_USER on the server's database.
set -a
. $CONFIG
set +a
[ "\$(id -u)" -eq 0 ] || { echo "tgk-server: run as root (sudo)." >&2; exit 1; }
exec runuser -u $SERVICE_USER -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR=$DATA/.bundle TGK_DB="\$TGK_DB" $PREFIX/current/tgk-server "\$@"
EOF
  chmod 0755 "$WRAPPER"
}

cmd_install() {
  local listen="" proxies="" auto=1 keep="" open_registration=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --listen) listen="${2:?--listen needs host:port}"; shift ;;
      --known-proxy) proxies="${proxies:+$proxies,}${2:?--known-proxy needs an address or network}"; shift ;;
      --no-auto-update) auto=0 ;;
      --keep-backups) keep="${2:?--keep-backups needs a number}"; shift ;;
      --open-registration) open_registration=1 ;;
      -h | --help) sed -n '2,15p' "$0"; return 0 ;;
      *) die "unknown option '$1' for install." ;;
    esac
    shift
  done
  need_root
  [ -d /run/systemd/system ] || die "systemd is not running here; tgk-server-manage needs it (for an LXC: a systemd-based template such as Debian or Ubuntu)."
  if ldd --version 2>&1 | grep -i musl > /dev/null; then
    die "this system uses musl (e.g. Alpine); tgk-server needs glibc: use a Debian, Ubuntu, Fedora or similar container."
  fi
  rid > /dev/null
  for tool in curl sha256sum flock runuser; do
    command -v "$tool" > /dev/null && continue
    if [ "$tool" = curl ] && command -v apt-get > /dev/null; then
      apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates > /dev/null
    elif [ "$tool" = curl ] && command -v dnf > /dev/null; then
      dnf install -y -q curl
    else
      die "'$tool' is missing; install it and run this again."
    fi
  done

  id "$SERVICE_USER" > /dev/null 2>&1 || useradd --system --no-create-home --home-dir "$DATA" --shell /usr/sbin/nologin "$SERVICE_USER"
  mkdir -p "$PREFIX/releases" "$DATA" "$CONFIG_DIR"
  chown "$SERVICE_USER:" "$DATA"
  chmod 700 "$DATA"
  backup_dirs

  if [ ! -f "$CONFIG" ]; then
    cat > "$CONFIG" <<EOF
# tgk-server settings (docs/SERVER.md). After a change: systemctl restart tgk-server
# Where it listens. Behind a reverse proxy on another machine (HAProxy, nginx, Caddy): 0.0.0.0, plus TGK_KNOWN_PROXIES.
TGK_URLS=http://127.0.0.1:5080
TGK_DB=$DATA/tgk.db
# Reverse proxies on other machines whose X-Forwarded-For/Proto are trusted (addresses or networks, comma-separated).
TGK_KNOWN_PROXIES=
# More options for "tgk-server serve", e.g. --rate-limit 40
TGK_EXTRA_ARGS=
# Daily database backups kept in $BACKUPS/daily
TGK_KEEP_BACKUPS=14
# 1: install new stable releases by themselves every night (same MAJOR version only); 0: only with "tgk-server-manage update"
TGK_AUTO_UPDATE=1
EOF
    chmod 640 "$CONFIG"
    chgrp "$SERVICE_USER" "$CONFIG"
    [ -n "$proxies" ] && [ -z "$listen" ] && listen="0.0.0.0:5080"
  fi
  [ -n "$listen" ] && set_config TGK_URLS "http://$listen"
  [ -n "$proxies" ] && set_config TGK_KNOWN_PROXIES "$proxies"
  [ -n "$keep" ] && set_config TGK_KEEP_BACKUPS "$keep"
  set_config TGK_AUTO_UPDATE "$auto"
  load_config

  local version
  version=$(installed_version)
  if [ -z "$version" ] || [ ! -x "$PREFIX/current/tgk-server" ]; then
    version=$(latest_version)
    log "Installing tgk-server $version ($(rid))"
    download_release "$version"
    switch_to "$version"
  else
    log "tgk-server $version is already installed; updating the configuration and services."
  fi
  install -m 0755 "$PREFIX/releases/$version/tgk-server-manage" "$SELF.new" && mv -f "$SELF.new" "$SELF"
  write_units

  local new_db=0
  [ -f "$TGK_DB" ] || new_db=1
  if [ "$new_db" -eq 1 ] && [ "$open_registration" -eq 0 ]; then
    # Reachable through a subdomain: nobody signs up uninvited. Accounts are made with "tgk-server user create".
    as_service "$PREFIX/current/tgk-server" registration off > /dev/null
  fi

  systemctl daemon-reload
  systemctl enable --now tgk-server-backup.timer > /dev/null
  if [ "$TGK_AUTO_UPDATE" = 1 ]; then
    systemctl enable --now tgk-server-update.timer > /dev/null
  else
    systemctl disable --now tgk-server-update.timer > /dev/null 2>&1 || true
  fi
  systemctl enable tgk-server > /dev/null
  systemctl restart tgk-server || true
  if ! wait_healthy "$version" && [ "$(systemctl show -p ExecMainStatus --value tgk-server)" = 226 ]; then
    # 226/NAMESPACE: the container can't set up the service's sandbox (an unprivileged LXC without "nesting").
    log "This container does not allow the service's sandbox (status 226/NAMESPACE); running tgk-server without it."
    log "To keep it: turn on 'nesting' for the container (Proxmox: Options -> Features), then delete $UNITS/tgk-server.service.d/container.conf"
    mkdir -p "$UNITS/tgk-server.service.d"
    printf '[Service]\nProtectSystem=false\nProtectHome=false\nPrivateTmp=false\n' > "$UNITS/tgk-server.service.d/container.conf"
    systemctl daemon-reload
    systemctl restart tgk-server || true
  fi
  if ! wait_healthy "$version"; then
    systemctl status tgk-server --no-pager -l >&2 || true
    journalctl -u tgk-server -n 40 --no-pager >&2 || true
    die "tgk-server did not answer at $(health_url) (its log is above)."
  fi

  log ""
  log "tgk-server $version is running on ${TGK_URLS} (database $TGK_DB)."
  [ -n "${TGK_KNOWN_PROXIES:-}" ] && log "Forwarded headers are trusted from: $TGK_KNOWN_PROXIES"
  log "Backups: daily in $BACKUPS/daily (last $TGK_KEEP_BACKUPS kept)."
  if [ "$TGK_AUTO_UPDATE" = 1 ]; then
    log "Updates: installed by themselves every night from the stable releases (tgk-server-manage status)."
  else
    log "Updates: by hand, with: tgk-server-manage update"
  fi
  if [ "$new_db" -eq 1 ]; then
    log ""
    if [ "$open_registration" -eq 0 ]; then
      log "Self-registration is off. Invite each user, then they choose \"Create an account\" in the app:"
    else
      log "Self-registration is on. To invite users instead: tgk-server registration off, then:"
    fi
    log "    tgk-server user create <name>"
  fi
}

cmd_status() {
  load_config
  log "Installed:   $(installed_version || true)"
  log "Latest:      $(latest_version 2>/dev/null || echo '(could not check)')"
  [ -f "$PREFIX/FAILED" ] && log "Skipped:     $(cat "$PREFIX/FAILED") (failed to start, rolled back)"
  log "Listening:   $TGK_URLS"
  log "Proxies:     ${TGK_KNOWN_PROXIES:-loopback only}"
  log "Service:     $(systemctl is-active tgk-server 2>/dev/null || true)"
  log "Auto-update: $([ "$TGK_AUTO_UPDATE" = 1 ] && echo on || echo off)"
  log "Last backup: $(ls -1t "$BACKUPS"/daily/tgk-*.db 2>/dev/null | head -n 1 || true)"
  log ""
  systemctl list-timers 'tgk-server-*' --no-pager 2>/dev/null || true
}

cmd_uninstall() {
  need_root
  systemctl disable --now tgk-server tgk-server-update.timer tgk-server-backup.timer > /dev/null 2>&1 || true
  rm -rf "$UNITS"/tgk-server.service "$UNITS"/tgk-server.service.d "$UNITS"/tgk-server-backup.{service,timer} "$UNITS"/tgk-server-update.{service,timer}
  systemctl daemon-reload
  rm -rf "$PREFIX" "$WRAPPER"
  log "Removed tgk-server. Kept: the database ($DATA), the backups ($BACKUPS) and the settings ($CONFIG)."
  rm -f "$SELF"
}

main() {
  local command="${1:-}"
  [ $# -gt 0 ] && shift
  case "$command" in
    install) cmd_install "$@" ;;
    update) cmd_update "$@" ;;
    backup) cmd_backup "$@" ;;
    status) cmd_status "$@" ;;
    uninstall) cmd_uninstall "$@" ;;
    *) sed -n '2,15p' "$0"; [ -z "$command" ] || [ "$command" = help ] || [ "$command" = --help ] || exit 2 ;;
  esac
}

main "$@"
