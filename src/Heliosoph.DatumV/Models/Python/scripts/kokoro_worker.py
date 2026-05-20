"""
Kokoro-82M TTS worker for Heliosoph.DatumV's Python bridge.

Wraps the `kokoro-onnx` Python package, which handles the phonemizer
(misaki) + ONNX inference pipeline end to end. The worker takes text
inputs and returns 24kHz WAV bytes.

Voices file layout
------------------
The C# registration's ``--voices-path`` may point to either:
  * A single bundled file (the canonical ``voices-v1.0.bin`` ~26 MB), OR
  * A directory of per-voice ``.bin`` files from the original hexgrad
    layout (e.g. ``af_bella.bin``, ``bm_george.bin``). Each per-voice
    file is a raw float32 array with no numpy header; we reshape to
    ``(length, 1, 256)`` which is what the v1 ONNX model expects.

Directory mode
--------------
``kokoro-onnx``'s ``Kokoro.__init__`` calls ``np.load(voices_path)``
directly — there's no method we can override to handle a directory. Our
workaround is to bundle the per-voice files into a temp ``.npz`` archive
at startup (cheap; ~26 MB total) and hand that to the constructor.
``np.load`` then sees a valid npz and returns a dict-like
``NpzFile`` that the rest of ``kokoro-onnx`` indexes normally.

Per-call overrides
------------------
The C# registration declares two optional positional arguments:
    [0] voice  (string)  -- e.g. "af_bella", "am_michael"
    [1] speed  (float)   -- 0.5 .. 2.0, default 1.0

So callers can do:
    SELECT models.kokoro_82m(text)                         -- defaults
    SELECT models.kokoro_82m(text, 'af_bella')             -- voice override
    SELECT models.kokoro_82m(text, 'bm_george', 1.2)       -- voice + speed

Setup
-----
    python -m venv .venv-kokoro
    .venv-kokoro\\Scripts\\pip install kokoro-onnx soundfile

CLI args (passed by C# via PythonBackedModel.scriptArgs)
--------------------------------------------------------
    --model-path     Absolute path to the Kokoro ONNX file
    --voices-path    Absolute path to voices.bin (single bundle) OR
                     a directory of per-voice .bin files
    --default-voice  Voice to use when the per-call override is empty
    --default-speed  Speed to use when the per-call override is empty
    --lang           Language tag (default "en-us")
"""

import argparse
import atexit
import io
import os
import struct
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import numpy as np  # noqa: E402  - sys.path needs to be set first
from kokoro_onnx import Kokoro  # noqa: E402

from python_worker_host import run  # noqa: E402


def _encode_wav_pcm16(samples, sample_rate):
    """Encode mono float [-1, 1] samples as a 16-bit PCM WAV byte string.

    We hand-roll the WAV header instead of using soundfile because the
    soundfile BytesIO write path goes through libsndfile's format
    detection, which flakes on some installs ('Format not recognised').
    The 44-byte RIFF/WAVE/fmt/data header is well-defined and the PCM_16
    payload is universal — any WAV decoder (including Heliosoph.DatumV's
    WhisperAudioInput) accepts it.
    """
    # 1D + contiguous, clamp + quantise to int16.
    arr = np.ascontiguousarray(samples, dtype=np.float32).squeeze()
    if arr.ndim != 1:
        # Multi-channel: downmix to mono by averaging channels.
        arr = arr.mean(axis=0) if arr.shape[0] < arr.shape[-1] else arr.mean(axis=-1)
    arr = np.clip(arr, -1.0, 1.0)
    pcm = (arr * 32767.0).astype(np.int16)
    data_bytes = pcm.tobytes()
    n_data = len(data_bytes)

    sr = int(sample_rate)
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", 36 + n_data, b"WAVE",
        b"fmt ", 16,                # fmt chunk size
        1, 1,                        # PCM format, mono
        sr, sr * 2,                  # sample rate, byte rate (sr * channels * bytes_per_sample)
        2, 16,                       # block align (channels * bytes_per_sample), bits per sample
        b"data", n_data,
    )
    return header + data_bytes


parser = argparse.ArgumentParser()
parser.add_argument("--model-path", required=True)
parser.add_argument("--voices-path", required=True)
parser.add_argument("--default-voice", default="af_bella")
parser.add_argument("--default-speed", type=float, default=1.0)
parser.add_argument("--lang", default="en-us")
ARGS = parser.parse_args()


def _load_per_voice_array(path):
    """Read one per-voice .bin into a (length, 1, 256) float32 array.

    Per-voice files from the original hexgrad layout are raw float32 bytes
    with no numpy header — exactly N*1024 bytes for N positions of (1, 256)
    style embeddings (the user's files are 524288 bytes = 512*1024). We
    deliberately do NOT try np.load() first: in some cases np.load picks
    up zip-like content and returns an int32 view, which then trips the
    ONNX session because the model expects float32 voice inputs. Treating
    these files as the raw float arrays they actually are sidesteps that
    failure mode.
    """
    raw = np.fromfile(path, dtype=np.float32)
    if raw.size == 0:
        raise RuntimeError(f"Voice file {path} is empty.")
    if raw.size % 256 != 0:
        raise RuntimeError(
            f"Voice file {path} has {raw.size * 4} bytes ({raw.size} float32 "
            f"elements); not divisible by 256. Expected raw-bytes "
            f"(length, 1, 256) layout from the hexgrad per-voice files."
        )
    length = raw.size // 256
    return raw.reshape(length, 1, 256)


def _bundle_voices_directory_to_npz(directory):
    """Bundle a directory of per-voice .bin files into a temp .npz file.

    Returns the path to the temp file. Registers an atexit handler to
    clean up the temp file when the worker shuts down.
    """
    voices = {}
    for filename in sorted(os.listdir(directory)):
        if not filename.endswith(".bin"):
            continue
        voice_name = os.path.splitext(filename)[0]
        voices[voice_name] = _load_per_voice_array(os.path.join(directory, filename))

    if not voices:
        raise RuntimeError(
            f"No .bin voice files found in {directory}. Expected files "
            f"like af_bella.bin, am_michael.bin, etc."
        )

    fd, temp_path = tempfile.mkstemp(suffix=".npz", prefix="kokoro_voices_")
    os.close(fd)
    np.savez(temp_path, **voices)

    def _cleanup():
        try:
            os.remove(temp_path)
        except OSError:
            pass

    atexit.register(_cleanup)
    return temp_path


# Resolve the voices argument: bundle to temp npz if it's a directory.
voices_argument = ARGS.voices_path
if os.path.isdir(ARGS.voices_path):
    voices_argument = _bundle_voices_directory_to_npz(ARGS.voices_path)

kokoro = Kokoro(ARGS.model_path, voices_argument)


_original_sess_run = kokoro.sess.run

# Build a name -> expected ORT type map once. We use this to coerce input
# dtypes that kokoro-onnx builds with the wrong type (notably 'speed',
# which some versions construct as int32 via np.array([1]) on Windows).
# ORT type strings look like "tensor(float)", "tensor(int64)", etc.
_expected_input_types = {
    inp.name: inp.type for inp in kokoro.sess.get_inputs()
}


def _coerce_dtype(value, expected_type):
    """If value is an ndarray with a dtype that doesn't match the model's
    declared input type, cast in place. Lists pass through unchanged —
    ORT handles list-to-tensor conversion based on the declared input."""
    if not hasattr(value, "dtype"):
        return value
    if expected_type == "tensor(float)" and value.dtype != np.float32:
        return value.astype(np.float32, copy=False)
    if expected_type == "tensor(double)" and value.dtype != np.float64:
        return value.astype(np.float64, copy=False)
    if expected_type == "tensor(int64)" and value.dtype != np.int64:
        return value.astype(np.int64, copy=False)
    if expected_type == "tensor(int32)" and value.dtype != np.int32:
        return value.astype(np.int32, copy=False)
    return value


def _patched_sess_run(output_names, input_feed, run_options=None):
    # Coerce mismatched dtypes before dispatch. Workaround for kokoro-onnx
    # passing 'speed' as int32 (np.array([speed]) without dtype on Windows
    # picks up int32 from the Python int default). The expected_input_types
    # map is queried by ORT itself for type checking; we just do the cast
    # eagerly so ORT sees the right thing.
    coerced_feed = {}
    for name, val in input_feed.items():
        expected = _expected_input_types.get(name, "")
        coerced = _coerce_dtype(val, expected)
        coerced_feed[name] = coerced
    return _original_sess_run(output_names, coerced_feed, run_options)


kokoro.sess.run = _patched_sess_run


def _pick_string(value, default):
    """Treat None / empty-string as 'use the default'."""
    if value is None or value == "":
        return default
    return value


def _pick_float(value, default):
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def infer(inputs, overrides):
    outputs = []
    for row_idx, row in enumerate(inputs):
        text = row[0] if row else ""
        row_overrides = overrides[row_idx] if row_idx < len(overrides) else []

        voice = _pick_string(
            row_overrides[0] if len(row_overrides) > 0 else None,
            ARGS.default_voice,
        )
        speed = _pick_float(
            row_overrides[1] if len(row_overrides) > 1 else None,
            ARGS.default_speed,
        )

        samples, sample_rate = kokoro.create(
            text, voice=voice, speed=speed, lang=ARGS.lang
        )

        # Encode to WAV bytes; the C# side carries them as DataKind.Image
        # (byte payload) until DataKind.Audio lands.
        outputs.append(_encode_wav_pcm16(samples, sample_rate))
    return outputs


run(infer)
