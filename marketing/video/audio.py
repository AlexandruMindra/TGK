"""Soundtrack: an original synthesized score, sound effects keyed to the animation, and the narration with the music
ducked under it.

usage: audio.py <lang>   (needs build/<lang>/{timing.json, narration.wav, sfx.json})  ->  build/<lang>/mix.wav
"""
import json, sys, wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
SR = 48000
BPM = 100
BEAT = 60 / BPM
BAR = 4 * BEAT
rng = np.random.default_rng(7)


def note(n):  # MIDI note -> Hz
    return 440.0 * 2 ** ((n - 69) / 12)


def fft_filter(x, lo=None, hi=None):
    """Zero-phase band filter in the frequency domain with soft (1-octave) edges."""
    n = len(x)
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(n, 1 / SR)
    g = np.ones_like(f)
    if hi:
        g *= 1 / np.sqrt(1 + (f / hi) ** 4)
    if lo:
        g *= 1 / np.sqrt(1 + (lo / np.maximum(f, 1e-3)) ** 4)
    return np.fft.irfft(X * g, n).astype(np.float32)


def reverb(x, seconds=2.6, wet=.28, seed=1):
    r = np.random.default_rng(seed)
    n = int(seconds * SR)
    t = np.arange(n) / SR
    ir = r.standard_normal(n) * np.exp(-t * 6.5 / seconds)
    ir = fft_filter(ir.astype(np.float32), hi=5000)
    ir[: int(.012 * SR)] = 0
    ir /= np.sqrt(np.sum(ir ** 2))
    size = 1 << int(np.ceil(np.log2(len(x) + n)))
    y = np.fft.irfft(np.fft.rfft(x, size) * np.fft.rfft(ir, size), size)[: len(x)]
    return (x * (1 - wet) + y.astype(np.float32) * wet * 1.6).astype(np.float32)


def env_adsr(n, a, r):
    e = np.ones(n, dtype=np.float32)
    na, nr = min(n, int(a * SR)), min(n, int(r * SR))
    e[:na] = np.linspace(0, 1, na)
    if nr:
        e[-nr:] *= np.linspace(1, 0, nr)
    return e


# ---------------------------------------------------------------- music
PROG = [  # (bass root, chord notes) Am - F - C - G, two bars each
    (45, [57, 60, 64, 69]), (41, [57, 60, 65, 69]), (48, [55, 60, 64, 67]), (43, [55, 59, 62, 67])]


def music(total, sections):
    n = int(total * SR) + SR
    L = np.zeros(n, np.float32)
    R = np.zeros(n, np.float32)
    t_all = np.arange(n) / SR

    def level(t, part):  # 0..1 intensity of a part at time t from the section map
        for a, b, lv in sections:
            if a <= t < b:
                return lv.get(part, 0)
        return 0

    nbars = int(total / BAR) + 2
    # pad: additive saw-ish voices, detuned left/right, one chord per two bars
    for b in range(0, nbars, 2):
        root, chord = PROG[(b // 2) % 4]
        t0 = b * BAR
        dur = 2 * BAR + 1.2
        i0, i1 = int(t0 * SR), min(n, int((t0 + dur) * SR))
        if i0 >= n:
            break
        lv = level(t0 + BAR, 'pad')
        if lv <= 0:
            continue
        tt = t_all[i0:i1] - t0
        e = env_adsr(i1 - i0, .9, 1.4) * lv
        for k, m in enumerate(chord):
            f = note(m)
            for side, det in ((L, -.12), (R, .12)):
                v = np.zeros(i1 - i0, np.float32)
                for h in range(1, 7):
                    v += np.sin(2 * np.pi * f * (1 + det / 100 * h) * h * tt + k + h).astype(np.float32) / h ** 1.3
                side[i0:i1] += v * e * .022
    # bass + arp + drums on the beat grid
    arp_pat = [0, 2, 3, 1, 2, 3, 0, 2]
    for b in range(nbars):
        root, chord = PROG[(b // 2) % 4]
        for q in range(16):  # 16th notes
            t0 = b * BAR + q * BEAT / 4
            i0 = int(t0 * SR)
            if i0 >= n - SR:
                continue
            # arp (16ths, plucked, with a dotted-eighth echo)
            lv = level(t0, 'arp')
            if lv > 0 and q % 2 == 0:
                m = chord[arp_pat[(q // 2) % 8]] + 12
                d = int(.32 * SR)
                tt = np.arange(d) / SR
                v = (np.sin(2 * np.pi * note(m) * tt) + .3 * np.sin(4 * np.pi * note(m) * tt)) * np.exp(-tt * 11)
                v = (v * lv * .05).astype(np.float32)
                pan = .5 + .25 * np.sin(q)
                for k, g in ((0, 1), (int(.75 * BEAT * SR), .4), (int(1.5 * BEAT * SR), .16)):
                    j = i0 + k
                    if j + d < n:
                        L[j:j + d] += v * g * (1 - pan) * 2 * (1 if k == 0 else .7)
                        R[j:j + d] += v * g * pan * 2
            # bass on 8ths
            lv = level(t0, 'bass')
            if lv > 0 and q % 2 == 0:
                d = int(BEAT / 2 * SR)
                tt = np.arange(d) / SR
                f = note(root - 12 if q % 8 else root - 12)
                v = (np.sin(2 * np.pi * f * tt) + .25 * np.sin(4 * np.pi * f * tt)) * env_adsr(d, .01, .12) * (1 - .55 * np.exp(-tt * 30))
                v = (v * lv * .11).astype(np.float32)
                L[i0:i0 + d] += v; R[i0:i0 + d] += v
            # kick on beats
            lv = level(t0, 'kick')
            if lv > 0 and q % 4 == 0:
                d = int(.35 * SR)
                tt = np.arange(d) / SR
                ph = 2 * np.pi * np.cumsum(48 + 110 * np.exp(-tt * 28)) / SR
                v = (np.sin(ph) * np.exp(-tt * 9) * lv * .32).astype(np.float32)
                L[i0:i0 + d] += v; R[i0:i0 + d] += v
            # hats on off 8ths
            lv = level(t0, 'hat')
            if lv > 0 and q % 4 == 2:
                d = int(.05 * SR)
                v = np.diff(rng.standard_normal(d + 1)).astype(np.float32) * np.exp(-np.arange(d) / SR * 90) * lv * .022
                L[i0:i0 + d] += v * .8; R[i0:i0 + d] += v
            # ticking clock in the cold open
            lv = level(t0, 'tick')
            if lv > 0 and q % 4 == 0:
                d = int(.03 * SR)
                tt = np.arange(d) / SR
                v = (np.sin(2 * np.pi * 2600 * tt) * np.exp(-tt * 160) * lv * .05).astype(np.float32)
                L[i0:i0 + d] += v; R[i0:i0 + d] += v
    # low drone under the cold open
    for a, b, lv in sections:
        if lv.get('drone'):
            i0, i1 = int(a * SR), int(b * SR)
            tt = t_all[i0:i1]
            v = (np.sin(2 * np.pi * note(33) * tt) * .5 + np.sin(2 * np.pi * note(45) * tt * 1.002) * .3) * env_adsr(i1 - i0, 1.5, 1) * lv['drone'] * .06
            L[i0:i1] += v.astype(np.float32); R[i0:i1] += v.astype(np.float32)
    L, R = reverb(L, seed=1), reverb(R, seed=2)
    return L, R


# ---------------------------------------------------------------- sound effects
def sfx_sound(kind, ev):
    def tt(d):
        return np.arange(int(d * SR)) / SR
    if kind == 'click':
        t = tt(.03)
        return (np.sin(2 * np.pi * 3200 * t) * np.exp(-t * 260) * .25 + rng.standard_normal(len(t)) * np.exp(-t * 500) * .08).astype(np.float32)
    if kind == 'key':
        t = tt(.05)
        return (np.sin(2 * np.pi * 900 * t) * np.exp(-t * 120) * .22 + rng.standard_normal(len(t)) * np.exp(-t * 300) * .06).astype(np.float32)
    if kind == 'tick':
        t = tt(.05)
        return (np.sin(2 * np.pi * 2400 * t) * np.exp(-t * 110) * .09).astype(np.float32)
    if kind == 'pop':
        t = tt(.12)
        f = 520 + 380 * (t / t[-1])
        return (np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 32) * .16).astype(np.float32)
    if kind == 'ding':
        t = tt(1.2)
        return ((np.sin(2 * np.pi * 1318.5 * t) + .6 * np.sin(2 * np.pi * 1975.5 * t)) * np.exp(-t * 5) * .06).astype(np.float32)
    if kind == 'alert':
        t = tt(1.0)
        a = np.sin(2 * np.pi * 1046.5 * t) * np.exp(-t * 6)
        b = np.zeros_like(t); k = int(.14 * SR); b[k:] = np.sin(2 * np.pi * 1568 * t[:-k]) * np.exp(-t[:-k] * 6)
        return ((a + b) * .07).astype(np.float32)
    if kind in ('whoosh', 'swish'):
        d = .9 if kind == 'whoosh' else .4
        t = tt(d)
        x = rng.standard_normal(len(t)).astype(np.float32)
        # sweep a one-pole low-pass up and down
        fc = 300 + 5200 * np.sin(np.pi * t / d) ** 2
        a = np.exp(-2 * np.pi * fc / SR)
        y = np.zeros_like(x); s = 0.0
        for i in range(len(x)):
            s = a[i] * s + (1 - a[i]) * x[i]; y[i] = s
        return (y * np.sin(np.pi * t / d) ** 2 * (.5 if kind == 'whoosh' else .32)).astype(np.float32)
    if kind == 'impact':
        t = tt(2.2)
        f = 40 + 90 * np.exp(-t * 12)
        boom = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 2.2)
        noise = fft_filter(rng.standard_normal(len(t)).astype(np.float32), hi=900) * np.exp(-t * 7)
        return (boom * .55 + noise * .35).astype(np.float32)
    if kind == 'riser':
        d = ev.get('dur', 1)
        t = tt(d)
        x = rng.standard_normal(len(t)).astype(np.float32)
        fc = 200 + 7000 * (t / d) ** 2
        a = np.exp(-2 * np.pi * fc / SR)
        y = np.zeros_like(x); s = 0.0
        for i in range(len(x)):
            s = a[i] * s + (1 - a[i]) * x[i]; y[i] = s
        return (y * (t / d) ** 2 * .35).astype(np.float32)
    if kind == 'type':
        cps, cnt = ev.get('cps', 20), ev.get('n', 10)
        out = np.zeros(int((cnt / cps + .1) * SR), np.float32)
        for k in range(cnt):
            j = int((k / cps + rng.uniform(-.008, .008)) * SR)
            t = tt(.025)
            c = (np.sin(2 * np.pi * rng.uniform(1700, 2600) * t) * np.exp(-t * 300) * .08 + rng.standard_normal(len(t)) * np.exp(-t * 600) * .05).astype(np.float32)
            j = max(0, j)
            out[j:j + len(c)] += c[: len(out) - j]
        return out
    return np.zeros(1, np.float32)


def main(lang):
    out = HERE / 'build' / lang
    timing = json.loads((out / 'timing.json').read_text())
    total = timing['total']
    sc = {s['id']: s for s in timing['scenes']}
    S = lambda i: sc[i]['start']
    q = lambda t: round(t / BAR) * BAR  # quantize section changes to bars
    full = {'pad': 1, 'arp': 1, 'bass': 1, 'kick': 1, 'hat': 1}
    sections = [
        (0, S('logo') - .2, {'drone': 1, 'tick': 1, 'pad': .35}),
        (S('logo') - .2, q(S('sessions')), {'pad': 1, 'arp': .7}),
        (q(S('sessions')), q(S('security')), full),
        (q(S('security')), q(S('sync')), {'pad': 1, 'arp': .8, 'bass': .8, 'hat': .6}),
        (q(S('sync')), q(S('community')), full),
        (q(S('community')), q(S('outro')), {**full, 'arp': 1.1}),
        (q(S('outro')), total + 5, {'pad': 1, 'arp': .6}),
    ]
    L, R = music(total, sections)
    n = len(L)

    # sound effects
    fx = np.zeros(n, np.float32)
    for ev in json.loads((out / 'sfx.json').read_text()):
        if ev['t'] < 0:
            continue
        x = sfx_sound(ev['type'], ev)
        i = int(ev['t'] * SR)
        if i < n:
            fx[i:i + len(x)] += x[: n - i]

    # narration and ducking
    with wave.open(str(out / 'narration.wav')) as w:
        v = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float32) / 32768
    voice = np.zeros(n, np.float32)
    voice[: min(n, len(v))] = v[:n]
    hop = int(.02 * SR)
    rms = np.sqrt(np.convolve(voice ** 2, np.ones(hop) / hop, 'same'))
    act = (rms > .02).astype(np.float32)
    # attack 60 ms, release 500 ms
    env = np.zeros(n, np.float32); e = 0.0
    a_at, a_rel = np.exp(-1 / (.06 * SR)), np.exp(-1 / (.5 * SR))
    step = 48  # compute at 1 kHz and hold
    for i in range(0, n, step):
        x = act[i]
        e = (a_at ** step) * e + (1 - a_at ** step) * x if x > e else (a_rel ** step) * e + (1 - a_rel ** step) * x
        env[i:i + step] = e
    duck = 1 - .5 * env
    fade = np.ones(n, np.float32)
    fe = int((total - 1.6) * SR)
    fade[fe:] = np.linspace(1, 0, n - fe) ** 1.5

    mus_gain = .85
    Lm = (L * duck * mus_gain + fx * .9 + voice) * fade
    Rm = (R * duck * mus_gain + fx * .9 + voice) * fade
    st = np.stack([Lm, Rm], 1)[: int(total * SR)]
    st /= max(1e-6, np.max(np.abs(st))) / .95
    with wave.open(str(out / 'mix.wav'), 'w') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((st * 32767).astype(np.int16).tobytes())
    # a music-only stem, handy for re-editing
    ms = np.stack([L, R], 1)[: int(total * SR)] * fade[: int(total * SR), None]
    ms /= max(1e-6, np.max(np.abs(ms))) / .9
    with wave.open(str(out / 'music.wav'), 'w') as w:
        w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((ms * 32767).astype(np.int16).tobytes())
    print(f'{lang}: mix.wav {total:.1f} s')


if __name__ == '__main__':
    main(sys.argv[1])
