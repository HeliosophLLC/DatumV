"""
Bark Small TTS worker for DatumIngest's Python bridge.

Wraps HuggingFace `transformers`' BarkModel — a 4-stage TTS pipeline
(text encoder → semantic → coarse → fine → audio decoder) that produces
24kHz mono speech with optional inline non-speech tokens
(``[laughs]``, ``[sighs]``, ``[music]``, etc.).

Auto-detects CUDA vs CPU at startup and falls back to CPU when torch
wasn't built with CUDA — so the worker doesn't crash on a fresh venv
that pip-installed the default (CPU-only) torch wheel. Re-run
``scripts/setup-bark-venv.ps1`` to rebuild the venv with the CUDA
wheel for the speed boost.

Setup
-----
Run from the repo root:
    .\\scripts\\setup-bark-venv.ps1

That creates ``$env:DATUM_MODELS\\.venv-bark`` with `transformers`,
`torch` (CUDA if available), and `scipy` installed.

Voice presets
-------------
Bark's output quality depends heavily on the speaker preset. Without
one, the model picks randomly per call -- some speakers are decent,
others produce noisy / shouted output. We pin a default
(``v2/en_speaker_6`` -- neutral male) and accept per-call overrides
via the first optional positional argument.

Other useful presets (see `Bark speaker library
<https://suno-ai.notion.site/8b8e8749ed514b0cbf3f699013548683>`_ for
the full list):

* ``v2/en_speaker_0`` -- male, calm, narrator-friendly
* ``v2/en_speaker_6`` -- male, neutral (default)
* ``v2/en_speaker_9`` -- female, expressive
* ``v2/de_speaker_3`` / ``v2/fr_speaker_2`` / etc. -- non-English

Per-call overrides
------------------
The C# registration declares one optional positional argument:
    [0] voice_preset (string) -- e.g. ``'v2/en_speaker_9'``

So callers can do:
    SELECT models.bark_small(text)                          -- default voice
    SELECT models.bark_small(text, 'v2/en_speaker_9')       -- override

Tips for good output
--------------------
* Use full sentences, not bare phrases. Bark's pipeline expects
  multi-second context; short inputs compress weirdly.
* Inline cues like ``[laughs]``, ``[clears throat]``, ``[sighs]`` work.
* Bark sometimes adds breath / room tone / bird sounds -- by design.
* Sampling is non-deterministic; same input produces different audio.

Output
------
PCM_16 mono WAV bytes at the model's configured sample rate (24kHz for
Bark Small). Carried by the C# side as ``DataKind.Image`` (byte
payload) until ``DataKind.Audio`` lands.
"""

import io
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402  - sys.path needs to be set first
import torch  # noqa: E402
from transformers import AutoProcessor, BarkModel  # noqa: E402

from python_worker_host import run  # noqa: E402


# Pick the best device the venv's torch was actually compiled with.
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
print(f"[bark] device={DEVICE}", file=sys.stderr, flush=True)

# Default voice preset. Pinning one prevents Bark from randomly picking
# unhinged speakers each call. v2/en_speaker_6 is the well-tested neutral
# male voice. Override per-call via the first optional positional arg.
DEFAULT_VOICE_PRESET = "v2/en_speaker_6"

processor = AutoProcessor.from_pretrained("suno/bark-small")
model = BarkModel.from_pretrained("suno/bark-small").to(DEVICE)

# Read the model's actual sample rate rather than hardcoding 24000 --
# robust if Suno ships a 16kHz or 48kHz variant later.
SAMPLE_RATE = int(model.generation_config.sample_rate)
print(f"[bark] sample_rate={SAMPLE_RATE}", file=sys.stderr, flush=True)


def _encode_wav_pcm16(samples, sample_rate):
    """Hand-roll a PCM_16 WAV. See kokoro_worker.py for the full rationale
    (soundfile.write to BytesIO is fragile under some libsndfile
    installs; the 44-byte RIFF/WAVE/fmt/data header avoids that path)."""
    arr = np.ascontiguousarray(samples, dtype=np.float32).squeeze()
    if arr.ndim != 1:
        arr = arr.mean(axis=0) if arr.shape[0] < arr.shape[-1] else arr.mean(axis=-1)
    arr = np.clip(arr, -1.0, 1.0)
    pcm = (arr * 32767.0).astype(np.int16)
    data_bytes = pcm.tobytes()
    n_data = len(data_bytes)

    sr = int(sample_rate)
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", 36 + n_data, b"WAVE",
        b"fmt ", 16,
        1, 1,                # PCM, mono
        sr, sr * 2,          # sample rate, byte rate
        2, 16,               # block align, bits per sample
        b"data", n_data,
    )
    return header + data_bytes


def _pick_string(value, default):
    """Treat None / empty-string as 'use the default'."""
    if value is None or value == "":
        return default
    return value


def infer(inputs, overrides):
    outputs = []
    for row_idx, row in enumerate(inputs):
        text = row[0] if row else ""
        row_overrides = overrides[row_idx] if row_idx < len(overrides) else []
        voice_preset = _pick_string(
            row_overrides[0] if len(row_overrides) > 0 else None,
            DEFAULT_VOICE_PRESET,
        )

        inputs_t = processor(
            text=[text], voice_preset=voice_preset, return_tensors="pt"
        ).to(DEVICE)
        audio_t = model.generate(**inputs_t)
        outputs.append(_encode_wav_pcm16(audio_t.cpu().numpy(), SAMPLE_RATE))
    return outputs


run(infer)
