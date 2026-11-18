#!/usr/bin/env bash
# End-to-end test of packaging/server/tgk-server-manage.sh on a disposable Linux machine with systemd (CI runs it on
# a GitHub runner): install, admin commands, backup, an update, a broken update that is rolled back, a skipped major
# update. It publishes tgk-server under made-up versions and serves them as fake releases. Needs root (sudo) and the
# .NET SDK. Never run it on a machine whose tgk-server you care about: it installs and uninstalls one.
set -euo pipefail
cd "$(dirname "$0")/.."

[ "$(id -u)" -eq 0 ] || { echo "run as root" >&2; exit 1; }
rid=linux-x64
work=$(mktemp -d)
releases="$work/releases"
port=8765
manage=/usr/local/sbin/tgk-server-manage

fail() { echo "FAIL: $*" >&2; journalctl -u tgk-server -n 40 --no-pager >&2 || true; exit 1; }
step() { echo; echo "==== $*"; }
# Checks that stdin contains $1, reading all of it (grep -q would stop early and fail the pipe under pipefail).
has() {
  local input
  input=$(cat)
  printf '%s\n' "$input"
  [[ "$input" == *"$1"* ]]
}
trap 'echo "FAIL: line $LINENO: $BASH_COMMAND" >&2; journalctl -u tgk-server -u tgk-server-backup -u tgk-server-update -n 40 --no-pager >&2 || true' ERR

publish() { # version
  dotnet publish src/TGK.Server -c Release -r "$rid" --self-contained -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:Version="$1" -o "$work/publish-$1" -v q -nologo > /dev/null
  release "$1" "$work/publish-$1/tgk-server"
}

release() { # version executable
  local dir="$releases/download/v$1"
  mkdir -p "$dir"
  cp "$2" "$dir/tgk-server-$rid"
  cp packaging/server/tgk-server-manage.sh "$dir/"
  echo "$1" > "$dir/tgk-server.version"
  (cd "$dir" && sha256sum "tgk-server-$rid" tgk-server-manage.sh > tgk-server-SHA256SUMS)
}

make_latest() { # version
  rm -rf "$releases/latest"
  mkdir -p "$releases/latest"
  ln -s "../download/v$1" "$releases/latest/download"
}

info() { curl -fsS http://127.0.0.1:5080/api/info; }

cleanup() {
  "$manage" uninstall > /dev/null 2>&1 || true
  rm -rf /var/lib/tgk /var/backups/tgk /etc/tgk
  userdel tgk 2> /dev/null || true
  kill "$server" 2> /dev/null || true
  rm -rf "$work"
}

step "Fake releases 0.0.1, 0.0.2, a broken 0.0.3 and 1.0.0"
publish 0.0.1
publish 0.0.2
publish 1.0.0
printf '#!/bin/sh\nif [ "$1" = --version ]; then echo 0.0.3; exit 0; fi\necho "broken on purpose" >&2\nexit 3\n' > "$work/broken"
chmod +x "$work/broken"
release 0.0.3 "$work/broken"
make_latest 0.0.1
(cd "$releases" && exec python3 -m http.server "$port" --bind 127.0.0.1 > /dev/null 2>&1) &
server=$!
trap cleanup EXIT
sleep 1
export TGK_RELEASE_BASE="http://127.0.0.1:$port"

step "Install"
bash packaging/server/tgk-server-manage.sh install --known-proxy 10.1.2.3 --keep-backups 3
[ "$(cat /opt/tgk/VERSION)" = 0.0.1 ] || fail "VERSION is not 0.0.1"
info | has '"version":"0.0.1"' || fail "0.0.1 is not answering"
info | has '"registrationOpen":false' || fail "registration should be off on a new install"
grep -q '^TGK_URLS=http://0.0.0.0:5080$' /etc/tgk/server.env || fail "a known proxy should make it listen on 0.0.0.0"
journalctl -u tgk-server --no-pager | has "trusted from loopback and 10.1.2.3" || fail "the proxy is not trusted"
systemctl is-enabled tgk-server-update.timer tgk-server-backup.timer > /dev/null || fail "timers are not enabled"
[ "$(stat -c %U:%a /var/lib/tgk/tgk.db)" = tgk:600 ] || fail "the database should be tgk's, mode 600"

step "Admin commands through the wrapper"
tgk-server user create alice | has "Invited 'alice'" || fail "user create"
tgk-server users | has alice || fail "users"

step "Backup"
systemctl start tgk-server-backup.service
ls /var/backups/tgk/daily/tgk-*.db > /dev/null || fail "no daily backup"
[ "$(stat -c %U:%a /var/backups/tgk/daily/tgk-"$(date +%Y-%m-%d)".db)" = tgk:600 ] || fail "the backup should be tgk's, mode 600"

step "Up to date"
"$manage" update --auto | has "up to date" || fail "0.0.1 should be up to date"

step "Update to 0.0.2"
make_latest 0.0.2
"$manage" update --auto
[ "$(cat /opt/tgk/VERSION)" = 0.0.2 ] || fail "VERSION is not 0.0.2"
info | has '"version":"0.0.2"' || fail "0.0.2 is not answering"
tgk-server users | has alice || fail "the data did not survive the update"
ls -d /var/backups/tgk/updates/*-before-0.0.2 > /dev/null || fail "no snapshot before the update"

step "A broken 0.0.3 is rolled back and then skipped"
make_latest 0.0.3
if "$manage" update --auto; then fail "the broken update should fail"; fi
[ "$(cat /opt/tgk/VERSION)" = 0.0.2 ] || fail "not rolled back to 0.0.2"
info | has '"version":"0.0.2"' || fail "0.0.2 is not answering after the rollback"
tgk-server users | has alice || fail "the data did not survive the rollback"
[ "$(cat /opt/tgk/FAILED)" = 0.0.3 ] || fail "0.0.3 should be marked as failed"
"$manage" update --auto | has "skipping it" || fail "the failed version should be skipped"

step "A new major version waits for a manual update"
make_latest 1.0.0
"$manage" update --auto | has "new major version" || fail "1.0.0 should not install by itself"
[ "$(cat /opt/tgk/VERSION)" = 0.0.2 ] || fail "1.0.0 was installed"
"$manage" update --force
info | has '"version":"1.0.0"' || fail "1.0.0 is not answering after update --force"

step "A tampered download is refused"
release 1.0.1 "$work/publish-1.0.0/tgk-server"
echo garbage >> "$releases/download/v1.0.1/tgk-server-$rid"
make_latest 1.0.1
if "$manage" update --auto; then fail "a file that fails its checksum should be refused"; fi
[ "$(cat /opt/tgk/VERSION)" = 1.0.0 ] || fail "the tampered version was installed"
info | has '"version":"1.0.0"' || fail "1.0.0 stopped answering"

step "Status"
"$manage" status

echo
echo "tgk-server-manage: all checks passed"
