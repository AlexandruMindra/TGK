#!/usr/bin/env bash
# Downloads what the video build needs but the repository does not carry: the Piper TTS runtime and voices, and the
# Inter / JetBrains Mono fonts. Everything goes into .cache/ (git-ignored). Safe to run again.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p .cache/voices .cache/fonts

[ -x .cache/venv/bin/piper ] || { python3 -m venv .cache/venv && .cache/venv/bin/pip install -q numpy piper-tts; }

hf=https://huggingface.co/rhasspy/piper-voices/resolve/main
for v in en/en_US/ryan/high/en_US-ryan-high ro/ro_RO/mihai/medium/ro_RO-mihai-medium; do
  n=$(basename "$v")
  [ -s ".cache/voices/$n.onnx" ] || curl -fsSL -o ".cache/voices/$n.onnx" "$hf/$v.onnx"
  [ -s ".cache/voices/$n.onnx.json" ] || curl -fsSL -o ".cache/voices/$n.onnx.json" "$hf/$v.onnx.json"
done

if [ ! -s .cache/fonts/InterVariable.ttf ]; then
  curl -fsSL -o .cache/inter.zip https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip
  unzip -q -o -j .cache/inter.zip InterVariable.ttf -d .cache/fonts
fi
if [ ! -s .cache/fonts/JetBrainsMono-Regular.woff2 ]; then
  curl -fsSL -o .cache/jbm.zip https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip
  unzip -q -o -j .cache/jbm.zip 'fonts/webfonts/JetBrainsMono-Regular.woff2' 'fonts/webfonts/JetBrainsMono-Bold.woff2' -d .cache/fonts
fi
cp ../../assets/fonts/*.ttf .cache/fonts/
[ -d node_modules/playwright ] || npm install --silent --no-save playwright@1.56.1 >/dev/null
echo "assets ready"
