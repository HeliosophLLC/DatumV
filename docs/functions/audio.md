---
title: Audio Functions
category: audio
---

# Audio Functions

[← Back to Functions](string.md) · [SQL Reference](../sql/select.md) · [Compute Backend](../compute.md)

## Metadata

### audio_sample_rate

`audio_sample_rate(audio)` → Int32

Sample rate in Hz, read from the audio value's inline metadata. [Elidable accessor](../technical/planner-time-elision.md) — the byte is stamped at ingest by the header parser, which recognises WAV (RIFF/WAVE), FLAC (STREAMINFO), MP3 (MPEG frame header, walking past any ID3v2 prefix), OGG Vorbis (identification packet), and OGG Opus (OpusHead, reported as 48000 since that's the actual decode rate regardless of the input-rate hint). Other containers return NULL until added to the parser; in that case `audio_samples` still decodes via FFmpeg.

```sql
SELECT audio_sample_rate(audio) FROM clips
```

## Loading & Decode

### audio_decode

`audio_decode(bytes)` → Audio

Wraps a raw encoded-audio byte array as a typed `Audio` value so downstream audio functions (`audio_sample_rate`, `audio_samples`, `audio_to_mono`, …) can consume it directly. The proximate use is in SQL recipes that compose `audio_decode(open_archive(:source, …).bytes)` to lift `.flac` or `.wav` entries pulled out of an archive into the engine's typed-audio surface without ever touching disk.

No PCM decoding happens here — the bytes pass through verbatim with the kind tag flipped to `Audio`. The container header is parsed (WAV, FLAC, MP3 with optional ID3v2 prefix, OGG Vorbis, OGG Opus) so the resulting value carries inline metadata that the audio accessor family reads without a full decode. WAV and FLAC expose sample rate / channels / bit depth / frame count; MP3 and OGG are lossy so bit depth and frame count surface as 0 (sample rate and channels are still meaningful and stamped). M4A and other containers fall through to the no-metadata path until added to the parser; in those cases `audio_samples` still decodes via FFmpeg.

```sql
-- Lift FLAC entries out of a LibriSpeech tarball into typed Audio values
SELECT path,
       audio_decode(bytes) AS clip,
       audio_sample_rate(audio_decode(bytes)) AS rate
FROM open_archive('LibriSpeech.tar.gz', path_pattern := '%.flac')

-- Common Voice — MP3 clips, sample rate stamped from the MPEG frame header
SELECT path, audio_decode(bytes) AS clip
FROM open_archive('cv-corpus-en.tar.gz', path_pattern := 'clips/%.mp3')

-- CTAS shape — the audio column carries inline metadata in the .datum file
CREATE TABLE librispeech AS
SELECT path,
       audio_decode(bytes) AS clip
FROM open_archive('LibriSpeech.tar.gz', path_pattern := '%.flac')
```

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
