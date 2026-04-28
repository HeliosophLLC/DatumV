---
title: Audio Functions
category: audio
---

# Audio Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

## Metadata

### audio_sample_rate

`audio_sample_rate(audio)` → Int32

Sample rate in Hz, read from the audio value's inline metadata. [Elidable accessor](../technical/planner-time-elision.md) — the byte is stamped at ingest by the WAV header parser; returns NULL for formats that haven't been parsed (MP3, FLAC, OGG until those are added to the header parser).

```sql
SELECT audio_sample_rate(audio) FROM clips
```

## Loading & Decode

### audio_samples

`audio_samples(rate, audio)` → Float32[]

Decode the audio container (WAV / MP3 / FLAC / OGG / M4A — anything FFmpeg's stock decoders recognise) to a flat Float32 array of PCM samples at the requested sample rate, mono. The audio analog of `image_to_tensor_chw` — the universal preprocessor for audio model bodies.

The IDE surfaces the canonical ML/broadcast rates as completion suggestions, but FFmpeg's resampler handles any positive Int32. Common rates: `8000` (telephony), `16000` (speech ML — Whisper, Silero VAD, Wav2Vec2, HuBERT), `22050` (librosa default), `24000` / `32000` (Encodec / MusicGen), `44100` (CD audio), `48000` (broadcast / CLAP / ImageBind audio).

Source channel counts > 1 raise — pipe through `audio_to_mono` first. Returns whatever PCM the source contained; no padding to a requested duration.

```sql
-- Already-mono source (a 16 kHz speech recording):
SELECT audio_samples(16000, clip) AS pcm FROM recordings

-- Stereo source — downmix first:
SELECT audio_samples(16000, audio_to_mono(clip)) AS pcm FROM podcasts
```

## Transforms

### audio_to_mono

`audio_to_mono(audio)` → Audio

Downmix any channel count to a single mono channel via libswresample's default mixer (the same coefficient set every reference audio ML pipeline uses), then re-encode as a 16-bit PCM WAV at the source's native sample rate. Mono sources flow through as a no-op transcoding pass.

Reach for this before `audio_samples` whenever the source is or might be stereo / multi-channel — `audio_samples` rejects non-mono input by design so that silent channel transformations don't slip into model preprocessing.

```sql
-- Canonical stereo-tolerant chain for any audio model:
SELECT audio_samples(16000, audio_to_mono(clip)) AS pcm FROM clips
```

## See Also

- [Vector & Tensor Functions](vector.md) — operations on tensors produced by `audio_samples`
- [Functions Reference](string.md) — complete function listing across all categories
