---
license: cc0-1.0
pretty_name: LJSpeech 1.1 (tar.gz re-encoding)
task_categories:
  - automatic-speech-recognition
  - text-to-speech
language:
  - en
size_categories:
  - 10K<n<100K
tags:
  - speech
  - audio
  - tts
  - asr
  - single-speaker
  - ljspeech
source_datasets:
  - original
---

# LJSpeech 1.1 — tar.gz re-encoding

A bit-for-bit re-encoding of [Keith Ito's LJSpeech 1.1](https://keithito.com/LJ-Speech-Dataset/) from its upstream `.tar.bz2` container into `.tar.gz`. The audio, transcripts, and directory layout are unchanged — only the outer compression wrapper differs.

Re-hosted under Heliosoph for ingestion-pipeline stability — Keith Ito's published archive is the authoritative source.

Credit: Keith Ito (LJSpeech 1.1, 2017) — recordings by LibriVox volunteers.

## Why a tar.gz mirror?

bzip2 decode is single-threaded and roughly 2–5× slower than gzip on modern hardware. For tools that materialize the archive end-to-end before reading (most database / dataset ingestion pipelines do this), the bz2 wrapper dominates install time.

The tradeoff is size: this gzip-wrapped archive is **~3.18 GB**, vs. the upstream bz2 at **~2.6 GB**. bzip2 typically compresses ~15% better than gzip on mixed content like WAV-plus-CSV, so you pay roughly 580 MB more download for a faster local decode. If bandwidth is your bottleneck, take the upstream; if decode time is, take this mirror.

## What this repo contains

```
LJSpeech-1.1/
├── README                # upstream Keith Ito notes (preserved verbatim)
├── metadata.csv          # 13,100 rows: <wav-id>|<original>|<normalized>
└── wavs/
    ├── LJ001-0001.wav    # 22.05 kHz, mono, 16-bit
    ├── LJ001-0002.wav
    └── …                 # 13,100 clips total, ~24 hours
```

Archive: `LJSpeech-1.1.tar.gz` — ~3.18 GB compressed (vs. upstream bz2 at ~2.6 GB).

## How to use

Stream the archive directly with any tar reader:

```python
import tarfile, soundfile as sf, io

with tarfile.open("LJSpeech-1.1.tar.gz", "r:gz") as tar:
    for member in tar:
        if not member.name.endswith(".wav"):
            continue
        with tar.extractfile(member) as f:
            audio, sr = sf.read(io.BytesIO(f.read()))
            # audio: float64 [-1, 1], sr: 22050
            ...
```

Or extract once and read flat:

```bash
tar -xzf LJSpeech-1.1.tar.gz
ls LJSpeech-1.1/wavs/ | head
```

## Audio specs

| | Spec |
|---|---|
| Format | RIFF WAV (PCM) |
| Sample rate | 22050 Hz |
| Channels | 1 (mono) |
| Bit depth | 16 |
| Total clips | 13,100 |
| Total duration | ~24 hours |
| Per-clip duration | ~1.1 – 10.1 sec (median ~6 sec) |
| Speaker | Single US English female |

## Transcripts

`metadata.csv` is a pipe-delimited file with three fields per row:

```
LJ001-0001|Printing, in the only sense with which we are at present concerned,…|Printing, in the only sense with which we are at present concerned,…
LJ001-0002|in being comparatively modern.|in being comparatively modern.
```

- Field 1: WAV identifier (matches the filename without extension).
- Field 2: original text as published.
- Field 3: normalized text (numbers expanded, abbreviations spelled out, punctuation simplified).

The same `metadata.csv` ships in the upstream archive — no edits.

## When to pick LJSpeech

- **Single-speaker TTS prototyping**: the canonical small-scale training set.
- **Vocoder development**: clean studio-quality input, consistent recording chain.
- **ASR sanity checks**: one voice, no channel variation — easy to hear what your model is doing.

For multi-speaker variation, noisy conditions, or published WER benchmarks, reach for **LibriSpeech** instead.

## License

**Public domain.** The source texts are 19th- and early-20th-century non-fiction passages whose copyrights have expired (United States); the audio was recorded by LibriVox volunteers and dedicated to the public domain by Keith Ito. No attribution required, but citing the upstream dataset is good practice — see `keithito.com/LJ-Speech-Dataset/`.

The HuggingFace metadata tags this `cc0-1.0` as the closest formal SPDX identifier for HF's taxonomy; the upstream dedication is plain "public domain" rather than formal CC0, but the effective rights are equivalent.

- Upstream: [keithito.com/LJ-Speech-Dataset](https://keithito.com/LJ-Speech-Dataset/)
- Upstream archive (bz2): [data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2](https://data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2)
