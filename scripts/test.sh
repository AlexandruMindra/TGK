#!/usr/bin/env bash
# Fast test run: builds once, then runs every xunit v3 test executable in parallel (no VSTest overhead).
# Usage: scripts/test.sh [--no-build] [xunit args, e.g. -class "*VaultTests" | -method "*Login*"]
# `dotnet test TGK.slnx` still works (and uses tests/tgk.runsettings); this is just ~4x faster.
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ "${1:-}" == "--no-build" ]]; then
  shift
else
  dotnet build TGK.slnx -v q -nologo 2>&1 | grep -E " error |Error\(s\)" || true
fi

logs=$(mktemp -d)
trap 'rm -rf "$logs"' EXIT
pids=()
names=()
for exe in tests/*/bin/Debug/net10.0/TGK.*.Tests; do
  name=$(basename "$exe")
  # -longRunning names any test still running after 20 s; timeout kills a hung assembly after 5 min.
  timeout 300 "$exe" -noColor -longRunning 20 "$@" > "$logs/$name.log" 2>&1 &
  pids+=($!)
  names+=("$name")
done

status=0
for i in "${!pids[@]}"; do
  if ! wait "${pids[$i]}"; then
    status=1
    echo "---- ${names[$i]} FAILED ----"
    grep -E "\[FAIL\]|Long Running Test|Assert|Exception" "$logs/${names[$i]}.log" | head -40
  fi
  tail -n1 "$logs/${names[$i]}.log"
done
exit $status
