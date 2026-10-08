#!/usr/bin/env bash
# Builds the TGK presentation video: ./build.sh [en|ro ...]   (default: en ro)
# Output per language in build/<lang>/: TGK-<lang>.mp4, thumbnail.png, subtitles.srt, youtube.md
set -euo pipefail
cd "$(dirname "$0")"
./fetch-assets.sh
PY=.cache/venv/bin/python
for lang in "${@:-en ro}"; do
  for l in $lang; do
    echo "== $l"
    $PY tts.py "$l"
    node render.mjs "$l" video --workers "${WORKERS:-4}"
    $PY audio.py "$l"
    ffmpeg -y -loglevel error -i "build/$l/video.mp4" -i "build/$l/mix.wav" \
      -af loudnorm=I=-14:TP=-1.5:LRA=11 -ar 48000 -c:v copy -c:a aac -b:a 256k -shortest -movflags +faststart "build/$l/TGK-$l.mp4"
    node render.mjs "$l" thumb
    $PY youtube.py "$l"
  done
done
