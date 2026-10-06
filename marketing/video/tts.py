"""Narration: speaks every sentence of narration.json with Piper and lays the scenes out on the timeline.

usage: tts.py <lang>   ->  build/<lang>/{timing.json, narration.wav, subtitles.srt, chapters.txt}

A scene lasts as long as its narration needs (lead + sentences + gaps + tail), never less than its minDur. Each
sentence's start within its scene is a "cue" the scene's animation keys to.
"""
import json, subprocess, sys, wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
SR = 48000
GAP, TAIL, LEAD = 0.38, 1.1, 0.7


def synth(text, model, length_scale, out):
    if out.exists():
        return
    subprocess.run([str(HERE / ".cache/venv/bin/piper"), "-m", str(HERE / f".cache/voices/{model}.onnx"),
                    "-f", str(out), "--length-scale", str(length_scale), "--sentence-silence", "0.25"],
                   input=text.encode(), check=True, capture_output=True)


def load(path):
    with wave.open(str(path)) as w:
        sr, n = w.getframerate(), w.getnframes()
        x = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768
    if sr != SR:  # linear resample is fine for speech at these rates
        t = np.arange(int(len(x) * SR / sr)) * sr / SR
        x = np.interp(t, np.arange(len(x)), x).astype(np.float32)
    # trim silence at both ends
    idx = np.where(np.abs(x) > 0.01)[0]
    if len(idx):
        x = x[max(0, idx[0] - int(0.03 * SR)): idx[-1] + int(0.08 * SR)]
    return x


def srt_time(t):
    ms = int(round(t * 1000))
    return f"{ms // 3600000:02}:{ms // 60000 % 60:02}:{ms // 1000 % 60:02},{ms % 1000:03}"


def wrap(text, width=44):
    words, lines, cur = text.split(), [], ""
    for w in words:
        if cur and len(cur) + 1 + len(w) > width:
            lines.append(cur)
            cur = w
        else:
            cur = f"{cur} {w}".strip()
    if cur:
        lines.append(cur)
    return lines


def chunks(text):
    """Splits a long sentence into subtitle cards of at most two lines, breaking after punctuation when possible."""
    lines = wrap(text)
    cards = [lines[i:i + 2] for i in range(0, len(lines), 2)]
    return ["\n".join(c) for c in cards]


def main(lang):
    spec = json.loads((HERE / "narration.json").read_text())
    voice = spec["voices"][lang]
    out = HERE / "build" / lang
    (out / "tts").mkdir(parents=True, exist_ok=True)

    scenes, clips, subs, t0 = [], [], [], 0.0
    for si, sc in enumerate(spec["scenes"]):
        t = sc.get("lead", LEAD)
        cues, ends = [], []
        for ji, s in enumerate(sc[lang]):
            wav = out / "tts" / f"{si:02}_{ji}.wav"
            synth(s.get("tts", s["text"]), voice["model"], voice["lengthScale"], wav)
            x = load(wav)
            d = len(x) / SR
            cues.append(round(t, 3))
            ends.append(round(t + d, 3))
            clips.append((t0 + t, x))
            # subtitles: split the sentence into cards, time them by length
            cards = chunks(s["text"])
            total = sum(len(c) for c in cards)
            ct = t0 + t
            for c in cards:
                cd = d * len(c) / total
                subs.append((ct, ct + cd, c))
                ct += cd
            t += d + GAP
        dur = round(max(sc["minDur"], t - GAP + TAIL), 3)
        scenes.append({"id": sc["id"], "start": round(t0, 3), "dur": dur, "cues": cues, "ends": ends,
                       "chapter": sc["chapter"][lang]})
        t0 += dur

    total = round(t0, 3)
    track = np.zeros(int(total * SR) + SR, dtype=np.float32)
    for start, x in clips:
        i = int(start * SR)
        track[i:i + len(x)] += x
    peak = np.max(np.abs(track)) or 1
    track = track / peak * 0.89
    with wave.open(str(out / "narration.wav"), "w") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((track * 32767).astype(np.int16).tobytes())

    (out / "timing.json").write_text(json.dumps({"lang": lang, "total": total, "scenes": scenes}, indent=1))
    with open(out / "subtitles.srt", "w") as f:
        for i, (a, b, c) in enumerate(subs, 1):
            f.write(f"{i}\n{srt_time(a)} --> {srt_time(min(b + 0.25, total))}\n{c}\n\n")
    with open(out / "chapters.txt", "w") as f:
        for s in scenes:
            m, sec = divmod(int(s["start"]), 60)
            f.write(f"{m}:{sec:02} {s['chapter']}\n")
    print(f"{lang}: {total:.1f} s, {len(scenes)} scenes")


if __name__ == "__main__":
    main(sys.argv[1])
