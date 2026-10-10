"""The one sound of his that the game has nothing like, synthesised from scratch: the snap."""
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


# His grunts and his powers use the game's own sounds (the Hutt's voice set, the Force powers'
# effects). Made here are the two the game has nothing like: the snap, and what he says after it.
def line():
    """'Perfectly balanced, as all things should be.' The words come from a text-to-speech voice
    (tools/chungus/voice_raw.wav, made once with voice.ps1); here they are slowed and dropped in
    pitch until they sound like something his size, doubled an octave down, and put in a hall."""
    import os
    with wave.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "voice_raw.wav"), "rb") as src:
        assert src.getframerate() == RATE and src.getnchannels() == 1 and src.getsampwidth() == 2
        raw = np.frombuffer(src.readframes(src.getnframes()), dtype="<i2").astype(float) / 32768.0
    # Lower and slower together, the way a slowed tape is: it is the size of him that sells it.
    deep = signal.resample_poly(raw, 100, 76)
    # An octave below, quietly, for chest.
    t = np.arange(len(deep)) / RATE
    hilbert = signal.hilbert(deep)
    phase = np.unwrap(np.angle(hilbert))
    under = np.abs(hilbert) * np.cos(phase / 2.0)
    under = signal.sosfilt(signal.butter(2, 500, btype="low", fs=RATE, output="sos"), under)
    voice = deep + 0.55 * under
    voice = signal.sosfilt(signal.butter(1, 3800, btype="low", fs=RATE, output="sos"), voice)
    # The hall: a second and a half of decaying noise, low-passed so the tail is dark.
    rng = np.random.default_rng(42)
    tail = rng.normal(0.0, 1.0, int(RATE * 1.5)) * np.exp(-np.arange(int(RATE * 1.5)) / RATE * 4.2)
    tail = signal.sosfilt(signal.butter(2, 2600, btype="low", fs=RATE, output="sos"), tail)
    wet = signal.fftconvolve(np.concatenate([voice, np.zeros(len(tail))]), tail)[:len(voice) + len(tail)]
    wet /= max(np.max(np.abs(wet)), 1e-9)
    dry = np.concatenate([voice, np.zeros(len(tail))])
    dry /= max(np.max(np.abs(dry)), 1e-9)
    return _fade(dry + 0.32 * wet, 0.01, 0.2)


SOUNDS = {
    "k_coop_chsnap": snap,
    "k_coop_chline": line,
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
