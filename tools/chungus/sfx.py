"""The boss's sound effects, synthesised from scratch (no samples): plain 16-bit mono WAV files."""
from __future__ import annotations

import io
import wave

import numpy as np
from scipy import signal

RATE = 22050


def _t(seconds):
    return np.arange(int(RATE * seconds)) / RATE


def _sweep(seconds, f0, f1, curve=1.0, shape="sine"):
    """A tone gliding from one pitch to another."""
    t = _t(seconds)
    f = f0 + (f1 - f0) * (t / seconds) ** curve
    phase = 2 * np.pi * np.cumsum(f) / RATE
    if shape == "saw":
        return signal.sawtooth(phase)
    if shape == "square":
        return signal.square(phase)
    return np.sin(phase)


def _noise(seconds, low=None, high=None, seed=0):
    rng = np.random.default_rng(seed)
    x = rng.normal(0.0, 1.0, int(RATE * seconds))
    if low and high:
        sos = signal.butter(2, [low, high], btype="band", fs=RATE, output="sos")
    elif high:
        sos = signal.butter(2, high, btype="low", fs=RATE, output="sos")
    elif low:
        sos = signal.butter(2, low, btype="high", fs=RATE, output="sos")
    else:
        return x
    return signal.sosfilt(sos, x)


def _decay(seconds, rate):
    return np.exp(-_t(seconds) * rate)


def _fade(x, attack=0.004, release=0.03):
    n_a, n_r = int(RATE * attack), int(RATE * release)
    x = x.copy()
    x[:n_a] *= np.linspace(0, 1, n_a)
    x[-n_r:] *= np.linspace(1, 0, n_r)
    return x


def _mix(seconds, *layers):
    """Layers are (start time, samples); the result is trimmed to the given length."""
    out = np.zeros(int(RATE * seconds))
    for start, samples in layers:
        a = int(RATE * start)
        b = min(a + len(samples), len(out))
        out[a:b] += samples[:b - a]
    return out


def _echo(x, delay, gain, repeats=3):
    out = x.copy()
    step = int(RATE * delay)
    for k in range(1, repeats + 1):
        if step * k >= len(x):
            break
        out[step * k:] += x[:len(x) - step * k] * gain ** k
    return out


def _voice(seconds, pitch0, pitch1, vowel):
    """A rough throat: a buzzing source pushed through two resonances that pick the vowel."""
    source = _sweep(seconds, pitch0, pitch1, shape="saw") + 0.25 * _noise(seconds, seed=7)
    out = np.zeros_like(source)
    for centre, width, gain in vowel:
        sos = signal.butter(2, [centre - width, centre + width], btype="band", fs=RATE, output="sos")
        out += gain * signal.sosfilt(sos, source)
    return out


OH = [(420, 90, 1.0), (850, 120, 0.6)]
AH = [(700, 110, 1.0), (1150, 140, 0.6)]
UH = [(330, 80, 1.0), (640, 100, 0.5)]


def snap():
    """The finger snap: a hard click, a held breath, then the floor drops out."""
    click = _noise(0.03, 1800, 5200, seed=1) * _decay(0.03, 160)
    crack = _sweep(0.02, 3200, 900) * _decay(0.02, 220)
    boom = _sweep(1.5, 78, 30, 0.5) * _decay(1.5, 2.6)
    rumble = _noise(1.5, None, 140, seed=2) * _decay(1.5, 2.0) * 3.0
    shimmer = _noise(1.3, 3500, 9000, seed=3) * np.linspace(0, 1, int(RATE * 1.3)) ** 2 * _decay(1.3, 1.2) * 0.35
    out = _mix(1.9, (0.0, click * 1.6), (0.0, crack), (0.16, boom * 1.3), (0.16, rumble), (0.2, shimmer))
    return _echo(out, 0.11, 0.3, 2)


def charge():
    """Stones lighting up: a rising, trembling hum."""
    t = _t(1.1)
    rise = _sweep(1.1, 110, 880, 1.6) + 0.5 * _sweep(1.1, 165, 1320, 1.6) + 0.3 * _sweep(1.1, 220, 1760, 1.6, "saw")
    tremolo = 0.7 + 0.3 * np.sin(2 * np.pi * (8 + 22 * t / 1.1) * t)
    return _fade(rise * tremolo * (t / 1.1) ** 1.2, 0.02, 0.05)


def blast():
    """The power stone firing: a falling shriek with a thump under it."""
    zap = _sweep(0.7, 1900, 90, 0.45, "saw") * _decay(0.7, 4.0)
    ring = _sweep(0.7, 2600, 200, 0.4) * _decay(0.7, 6.0) * 0.6
    burst = _noise(0.35, 400, 6000, seed=4) * _decay(0.35, 12.0)
    thump = _sweep(0.5, 120, 40) * _decay(0.5, 7.0) * 1.4
    return _echo(_mix(0.95, (0, zap), (0, ring), (0, burst), (0.02, thump)), 0.09, 0.25, 2)


def slam():
    """A very heavy rabbit landing fist first."""
    thud = _sweep(1.0, 95, 28, 0.4) * _decay(1.0, 3.5) * 1.6
    crunch = _noise(0.45, 150, 2600, seed=5) * _decay(0.45, 9.0)
    debris = _noise(0.9, 900, 5000, seed=6) * _decay(0.9, 4.0) * 0.25
    return _mix(1.1, (0, thud), (0, crunch), (0.08, debris))


def warp():
    """The space stone: the air folds and unfolds."""
    t = _t(0.75)
    wobble = np.sin(2 * np.pi * np.cumsum(300 + 900 * np.sin(np.pi * t / 0.75) ** 2 + 60 * np.sin(2 * np.pi * 17 * t)) / RATE)
    air = _noise(0.75, 1200, 6000, seed=8) * np.sin(np.pi * t / 0.75) ** 2 * 0.35
    return _fade(wobble * np.sin(np.pi * t / 0.75) ** 0.6 + air, 0.01, 0.06)


def laugh():
    """A deep, pleased 'hoh hoh hoh hoh'."""
    layers = []
    start = 0.0
    for k, (length, pitch) in enumerate([(0.2, 150), (0.18, 138), (0.18, 128), (0.3, 112)]):
        ho = _voice(length, pitch, pitch * 0.78, OH) * np.sin(np.pi * _t(length) / length) ** 0.7
        layers.append((start, ho))
        start += length + 0.07
    return _fade(_mix(start + 0.05, *layers), 0.005, 0.04)


def hurt():
    """A short surprised 'oof'."""
    t = _t(0.3)
    return _fade(_voice(0.3, 190, 105, UH) * np.sin(np.pi * t / 0.3) ** 0.5, 0.004, 0.05)


def intro():
    """His entrance: two drum hits and a low brass chord swelling up."""
    layers = []
    for start in (0.0, 0.45):
        layers.append((start, _sweep(0.6, 110, 45, 0.5) * _decay(0.6, 6.0) * 1.5))
        layers.append((start, _noise(0.2, 200, 1800, seed=9) * _decay(0.2, 18.0) * 0.5))
    t = _t(2.0)
    chord = sum(_sweep(2.0, f, f, shape="saw") * g for f, g in ((73.4, 1.0), (110.0, 0.8), (146.8, 0.7), (174.6, 0.6), (220.0, 0.4)))
    sos = signal.butter(2, 900, btype="low", fs=RATE, output="sos")
    chord = signal.sosfilt(sos, chord) * np.minimum(t / 0.6, 1.0) * np.exp(-np.maximum(t - 1.3, 0) * 3.0) * 0.5
    layers.append((0.9, chord))
    return _fade(_mix(3.0, *layers), 0.002, 0.1)


def die():
    """A long falling whistle, a groan, and the landing."""
    whistle = _sweep(1.2, 1500, 260, 0.8) * (0.8 + 0.2 * np.sin(2 * np.pi * 9 * _t(1.2))) * 0.6
    groan = _voice(1.0, 140, 70, AH) * np.sin(np.pi * _t(1.0) / 1.0) ** 0.6 * 0.8
    thud = _sweep(0.8, 90, 30, 0.4) * _decay(0.8, 4.0) * 1.6
    crunch = _noise(0.3, 150, 2000, seed=10) * _decay(0.3, 10.0)
    return _mix(2.2, (0.0, _fade(whistle, 0.02, 0.05)), (0.25, groan), (1.25, thud), (1.25, crunch))


SOUNDS = {
    "k_coop_chsnap": snap,
    "k_coop_chcharge": charge,
    "k_coop_chblast": blast,
    "k_coop_chslam": slam,
    "k_coop_chwarp": warp,
    "k_coop_chlaugh": laugh,
    "k_coop_chhurt": hurt,
    "k_coop_chintro": intro,
    "k_coop_chdie": die,
}


def wav_bytes(samples):
    peak = float(np.max(np.abs(samples))) or 1.0
    pcm = np.round(samples / peak * 0.89 * 32767).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(pcm.tobytes())
    return buffer.getvalue()
