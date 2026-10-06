# TGK presentation video

The YouTube presentation of TGK, built entirely from code: every frame is HTML rendered by headless Chromium, the
narration is synthesized with [Piper](https://github.com/rhasspy/piper) (offline neural TTS), and the music and sound
effects are synthesized in Python. Nothing is recorded by hand, so the video can be rebuilt after the app changes.

```bash
marketing/video/build.sh          # English and Romanian
marketing/video/build.sh en       # one language
```

Needs Node.js with Playwright's Chromium, ffmpeg, Python 3 and network access the first time (`fetch-assets.sh` puts
the Piper voices and fonts into `.cache/`). Output per language in `build/<lang>/`:

| File | |
|---|---|
| `TGK-<lang>.mp4` | 1920x1080, 30 fps, H.264 + AAC, loudness-normalized to -14 LUFS (YouTube's target) |
| `thumbnail.png` | 1280x720 YouTube thumbnail |
| `subtitles.srt` | Subtitles timed to the narration |
| `youtube.md` | Title, description with chapters, tags |
| `music.wav` | The score alone, for re-editing |

## How it fits together

| File | Role |
|---|---|
| `narration.json` | The script: one entry per scene, a sentence per cue, in each language (`tts` spells out acronyms for the voice) |
| `tts.py` | Speaks every sentence and lays the scenes out: a scene lasts as long as its narration needs (`timing.json`) |
| `stage/` | The scenes. `app.js` rebuilds the TGK window from `docs/images`; `scenes1.js` / `scenes2.js` animate each scene as a pure function of time; `strings.js` holds the on-screen text |
| `render.mjs` | Renders frames (`video`), single frames for review (`stills`), the thumbnail (`thumb`) or the sound-effect cues (`sfx`) |
| `audio.py` | Score (A minor, 100 BPM), sound effects at the animation's cues, narration with the music ducked under it |

Review a scene without rendering the whole video: `./stills.sh en files .2 .5 .9` (fractions of the scene), or open
`stage/index.html?preview=1&lang=en` through any static server rooted at the repository.

To change what is said, edit `narration.json` (and `strings.js` for on-screen text); the timing follows by itself.
