#!/usr/bin/env bash
# Wraps a macOS publish folder into TGK.app and packs it:
#   TGK-<rid>.tar.gz  the bundle (what installed clients update themselves from: docs/RELEASING.md)
#   TGK-<rid>.dmg     the same bundle with an Applications link, for a first install (only where hdiutil exists)
# Usage: packaging/macos/build-app.sh <publish-dir> <version> <rid> <out-dir>
set -euo pipefail
publish=$1 version=$2 rid=$3 out=$4
root="$(cd "$(dirname "$0")/../.." && pwd)"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"
out="$(cd "$out" && pwd)"

app="$work/TGK.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
cp "$root/assets/icon.icns" "$app/Contents/Resources/icon.icns"
short=${version%%-*} # CFBundleShortVersionString must be X.Y.Z
sed -e "s/@SHORT_VERSION@/$short/g" -e "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/TGK"

# Apple Silicon runs only signed native code: sign every Mach-O file ad hoc (no Apple ID involved), then the bundle.
# Without a Developer ID and notarization, a downloaded copy needs "Open" from the context menu the first time.
if command -v codesign > /dev/null; then
  while IFS= read -r -d '' f; do
    if file -b "$f" | grep -q "Mach-O"; then
      codesign --force --sign - "$f"
    fi
  done < <(find "$app/Contents/MacOS" -type f -print0)
  codesign --force --sign - "$app" || echo "::warning::Could not sign the TGK.app bundle as a whole (its binaries are signed)."
fi

# No AppleDouble (._*) files or extended attributes in the archive.
(cd "$work" && COPYFILE_DISABLE=1 tar --no-xattrs -czf "$out/TGK-$rid.tar.gz" TGK.app 2>/dev/null \
  || COPYFILE_DISABLE=1 tar -czf "$out/TGK-$rid.tar.gz" TGK.app)

if command -v hdiutil > /dev/null; then
  dmg="$work/dmg"
  mkdir -p "$dmg"
  cp -R "$app" "$dmg/"
  ln -s /Applications "$dmg/Applications"
  hdiutil create -volname "TGK" -srcfolder "$dmg" -ov -format UDZO "$out/TGK-$rid.dmg" > /dev/null
fi
echo "Packed TGK $version for $rid into $out"
