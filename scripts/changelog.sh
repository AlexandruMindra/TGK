#!/usr/bin/env bash
# Prints the CHANGELOG.md section of a version (the text under "## [X.Y.Z] - date", without the heading), or of
# "Unreleased". Fails when the section is missing or empty: a release needs its notes (docs/RELEASING.md).
# Usage: scripts/changelog.sh <X.Y.Z | Unreleased>
set -euo pipefail
cd "$(dirname "$0")/.."
section=$1
notes=$(awk -v want="$section" '
  /^## \[/ { if (found) exit; found = index($0, "## [" want "]") == 1; next }
  /^\[[^]]+\]: / { if (found) exit }
  found { print }
' CHANGELOG.md | sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}')
if [[ -z "${notes//[[:space:]]/}" ]]; then
  echo "CHANGELOG.md has no notes under \"## [$section]\"." >&2
  exit 1
fi
printf '%s\n' "$notes"
