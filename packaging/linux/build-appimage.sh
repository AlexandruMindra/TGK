#!/usr/bin/env bash
# Builds TGK-x86_64.AppImage (one file: download, chmod +x, run) from a linux-x64 publish folder.
# Installed AppImages update themselves by replacing the file (docs/RELEASING.md).
# Usage: packaging/linux/build-appimage.sh <publish-dir> <version> <out-dir>
set -euo pipefail
publish=$1 version=$2 out=$3
root="$(cd "$(dirname "$0")/../.." && pwd)"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"
out="$(cd "$out" && pwd)"

appdir="$work/TGK.AppDir"
mkdir -p "$appdir/usr/lib/tgk" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -R "$publish"/. "$appdir/usr/lib/tgk/"
chmod +x "$appdir/usr/lib/tgk/TGK" "$appdir/usr/lib/tgk/tgk-mcp"
cp "$root/packaging/linux/AppRun" "$appdir/AppRun"
cp "$root/packaging/linux/tgk.desktop" "$appdir/tgk.desktop"
cp "$root/packaging/linux/tgk.desktop" "$appdir/usr/share/applications/tgk.desktop"
cp "$root/assets/icon.png" "$appdir/tgk.png"
cp "$root/assets/icon.png" "$appdir/usr/share/icons/hicolor/256x256/apps/tgk.png"
ln -s tgk.png "$appdir/.DirIcon"

tool="${APPIMAGETOOL:-$work/appimagetool}"
if [[ ! -x "$tool" ]]; then
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi
# No FUSE needed to run appimagetool itself (CI containers).
ARCH=x86_64 VERSION="$version" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out/TGK-x86_64.AppImage" > "$work/appimagetool.log" 2>&1 \
  || { cat "$work/appimagetool.log"; exit 1; }
echo "Built $out/TGK-x86_64.AppImage ($version)"
