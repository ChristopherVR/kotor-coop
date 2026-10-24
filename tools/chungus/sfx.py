"""Original nonverbal Cairn Sentinel sounds, synthesised from scratch."""
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


def snap():
    """Resonator discharge: detuned mineral tones and a descending pressure wave."""
    t = _t(1.9)
    body = sum(np.sin(2*np.pi*f*t)*np.exp(-t*d) for f,d in ((83,2.8),(139,3.5),(227,4.5)))
    return _fade(_echo(body + 0.15*_noise(1.9,150,900,seed=41)*np.exp(-t*5),0.14,0.25))


def line():
    """Nonverbal answering chime, synthesised entirely from oscillators; no sampled voice."""
    t = _t(2.4)
    sound = np.zeros(len(t))
    for start,freq in ((0,311),(0.24,389),(0.55,467)):
        local=np.maximum(0,t-start)
        sound += (t>=start)*np.sin(2*np.pi*freq*local)*np.exp(-local*3.2)
    return _fade(_echo(sound,0.17,0.3),0.01,0.15)


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
