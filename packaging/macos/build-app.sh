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

# Apple Silicon runs only signed native code. The SDK signs the executable and the NuGet libraries come signed; sign
# (ad hoc, no Apple ID involved) any Mach-O file that is not. This happens before the files go into the bundle:
# inside it, codesign would treat the executable as the bundle's and demand signatures for the .dll files next to it.
# The bundle as a whole stays unsigned, so without a Developer ID and notarization a downloaded copy has to be
# allowed once (context menu → Open, or System Settings → Privacy & Security).
payload="$work/payload"
cp -R "$publish" "$payload"
if command -v codesign > /dev/null; then
  while IFS= read -r -d '' f; do
    if file -b "$f" | grep -q "Mach-O" && ! codesign --verify "$f" 2> /dev/null; then
      codesign --force --sign - "$f"
    fi
  done < <(find "$payload" -type f -print0)
fi

app="$work/TGK.app"
mkdir -p "$app/Contents/Resources"
mv "$payload" "$app/Contents/MacOS"
cp "$root/assets/icon.icns" "$app/Contents/Resources/icon.icns"
short=${version%%-*} # CFBundleShortVersionString must be X.Y.Z
sed -e "s/@SHORT_VERSION@/$short/g" -e "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/TGK" "$app/Contents/MacOS/tgk-mcp" # tgk-mcp: what agents run (docs/AGENTS.md)

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
