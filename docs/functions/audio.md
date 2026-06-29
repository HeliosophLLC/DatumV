---
title: Audio Functions
category: audio
---

# Audio Functions

## Metadata

### audio_sample_rate

`audio_sample_rate(audio)` → Int32

Sample rate in Hz, read from the audio value's inline metadata. [Elidable accessor](../technical/planner-time-elision.md) — the byte is stamped at ingest by the header parser, which recognises WAV (RIFF/WAVE), FLAC (STREAMINFO), MP3 (MPEG frame header, walking past any ID3v2 prefix), OGG Vorbis (identification packet), and OGG Opus (OpusHead, reported as 48000 since that's the actual decode rate regardless of the input-rate hint). Other containers return NULL until added to the parser; in that case `audio_samples` still decodes via FFmpeg.

```sql
SELECT audio_sample_rate(audio) FROM clips
```

### audio_duration

`audio_duration(audio)` → Float64

Clip length in seconds. [Elidable accessor](../technical/planner-time-elision.md) — computed inline as `frame_count ÷ sample_rate` (exact) when both are stamped at ingest, which covers WAV and FLAC. For containers whose frame count isn't surfaced by the header parser (MP3, OGG), it falls back to a decode-free read of the container's recorded duration. Returns NULL only when no duration is recorded.

```sql
SELECT path FROM clips WHERE audio_duration(audio) > 30.0
```

## Loading & Decode

### audio_decode

`audio_decode(bytes)` → Audio

Wraps a raw encoded-audio byte array as a typed `Audio` value so downstream audio functions (`audio_sample_rate`, `audio_samples`, `audio_to_mono`, …) can consume it directly. The proximate use is in SQL recipes that compose `audio_decode(open_archive(:source, …).bytes)` to lift `.flac` or `.wav` entries pulled out of an archive into the engine's typed-audio surface without ever touching disk.

No PCM decoding happens here — the bytes pass through verbatim with the kind tag flipped to `Audio`. The container header is parsed (WAV, FLAC, MP3 with optional ID3v2 prefix, OGG Vorbis, OGG Opus) so the resulting value carries inline metadata that the audio accessor family reads without a full decode. WAV and FLAC expose sample rate / channels / bit depth / frame count; MP3 and OGG are lossy so bit depth and frame count surface as 0 (sample rate and channels are still meaningful and stamped). M4A and other containers fall through to the no-metadata path until added to the parser; in those cases `audio_samples` still decodes via FFmpeg.

`audio_decode` is intentionally **permissive** on unrecognised input: bytes that don't match any known container signature still produce a non-NULL `Audio` value, just with zero-sentinel inline metadata, so `audio_sample_rate()` and friends return `NULL` rather than throwing. For a validated conversion that throws at the call site with a hex-byte preview of the mismatch, use **`CAST(bytes AS Audio)`** instead — see [Type System](../sql/type-system.md). The validated form accepts WAV / FLAC / MP3 / OGG (Vorbis or Opus) headers; `try_cast(bytes AS Audio)` returns typed `NULL` on failure, and `can_cast(bytes, Audio)` returns a boolean usable as a `WHERE`-clause filter to drop bad rows in a folder scan.

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

## ONNX Feature Extraction

### audio_to_log_mel

`audio_to_log_mel(samples, n_mels)` → `Float32[]`

Whisper-canonical log-mel spectrogram feature extractor — STFT
(`n_fft = 400`, `hop = 160`, Hann window, center-padded), Slaney mel
filterbank with `n_mels` channels, log10, max-8 dB clip + shift to
roughly `[-1, 1]`. The input `samples` is the raw PCM `Float32[]`
that [`audio_samples`](#audio_samples) emits at 16 kHz.

Output is a flat mel-major `Float32[]` of length `n_mels × 3000`
(the trailing 3000 is Whisper's 30-second encoder context — clips
longer than that truncate, shorter zero-pad to the limit). Pass
straight to the encoder ONNX with shape
`[1, n_mels, 3000]`:

```sql
DECLARE samples Float32[] = audio_samples(16000, audio_to_mono(clip));
DECLARE mel Float32[] = audio_to_log_mel(samples, 80::Int32);
DECLARE encoder_features Float32[] = infer(
    'encoder', mel, [1::Int32, 80::Int32, 3000::Int32]);
```

`n_mels` is 80 for Whisper base / small / medium / large-v1/v2;
**128** for Whisper large-v3 (and the v3 turbo distillation). Always
match the value to the encoder's expected input depth — passing 80
to a large-v3 export silently produces garbage transcriptions.

Canonical use: `models.whisper_base`.

## Visualization

The waveform-visualisation stack is a four-layer cake: a numeric envelope primitive, a per-column lambda renderer for custom styles, a single-path filled-outline renderer, and a one-liner sugar wrapper for the common case. Use the layer that matches how much control you need.

### audio_waveform_envelope

`audio_waveform_envelope(audio, bins)` → Array&lt;Float32&gt;(bins, 2)

Folds the decoded audio into `bins` evenly-sized bins and emits the per-bin **peak envelope** as a shape-aware 2-D Float32 array — column 0 is the bin minimum, column 1 the bin maximum, both in nominal `[-1, 1]` amplitude space. Multi-channel sources auto-downmix to mono via libswresample's default mixer. Empty bins (when `bins` exceeds the decoded sample count) emit `(0, 0)`.

This is the numeric substrate beneath every other waveform function. Use it directly when you want analysis, ML features, or a custom rendering path; pipe it into `audio_waveform_drawing` / `audio_waveform_path` for the bundled visual primitives.

```sql
-- 800 bins for an 800-pixel-wide hero image
SELECT audio_waveform_envelope(clip, 800) AS env FROM clips
```

`bins` must be positive. Null audio or null bins produces a null Float32 array.

### audio_waveform_drawing

`audio_waveform_drawing(envelope, render Lambda<waveform, Drawing>)` → Drawing

Walks a precomputed `(bins, 2)` envelope and invokes the user's lambda once per bin, assembling the per-bin Drawings into a single `draw_group`. The `render` lambda is scoped to the **waveform context** (see [Lambda Expressions](../sql/lambda-expressions.md)) and receives:

- `t Float32` — the column's normalised position in `[0, 1]` (endpoint-inclusive: bin 0 sees `t = 0`, bin `(bins - 1)` sees `t = 1`).
- `min Float32` — the bin's minimum amplitude in `[-1, 1]`.
- `max Float32` — the bin's maximum amplitude in `[-1, 1]`.

Width and height aren't lambda parameters: the canvas dimensions are expressions the caller already names at the call site, and the lambda body captures them by closure. The same envelope can therefore drive renderings at different resolutions by varying only the lambda.

`WaveformContext` inherits from `AnimationContext`, so every animation curve (`lerp`, `oscillate`, `wobble`, `bounce`, `fade_in`, `fade_out`, `random_walk`) is callable on `t` inside the lambda, alongside every drawing primitive.

```sql
-- Classic Audacity-style vertical bars
SELECT render(
    draw_group([
        draw_rect(point2d(0, 0), point2d(1200, 240), color_hex('#0d1117')),
        audio_waveform_drawing(
            audio_waveform_envelope(clip, 1200),
            (t, lo, hi) -> draw_line(
                point2d(t * 1200, 120 - hi * 120),
                point2d(t * 1200, 120 - lo * 120),
                color_hex('#7fdbff'), 1))
    ]),
    point2d(1200, 240)) AS hero
FROM clips
```

```sql
-- Horizontal cyan → magenta gradient via color_interpolate
audio_waveform_drawing(env, (t, lo, hi) -> draw_line(
    point2d(t * 1200, 120 - hi * 120),
    point2d(t * 1200, 120 - lo * 120),
    color_interpolate(color_hex('#00d4ff'), color_hex('#ff00aa'), t), 1))
```

```sql
-- Per-bar vertical gradient (bright top, dim bottom) via draw_line's five-arg form
audio_waveform_drawing(env, (t, lo, hi) -> draw_line(
    point2d(t * 1200, 120 - hi * 120),
    point2d(t * 1200, 120 - lo * 120),
    color_hex('#00d4ff'),   -- start (top)
    color_hex('#1a3a5c'),   -- end (bottom)
    1))
```

```sql
-- Mirrored / asymmetric: top half cyan, bottom half rose
audio_waveform_drawing(env, (t, lo, hi) -> draw_group([
    draw_line(point2d(t*1200, 120 - hi*120), point2d(t*1200, 120), color_hex('#7fdbff'), 1),
    draw_line(point2d(t*1200, 120),          point2d(t*1200, 120 - lo*120), color_hex('#ff5577'), 1)]))
```

Null envelope or null lambda produces a null Drawing. A lambda call that returns null skips that bin from the group; the remaining bins still compose. Envelopes that aren't shape `(bins, 2)` raise — produce one via `audio_waveform_envelope`. Smoothing across columns isn't a per-bin operation, so apply it to the envelope before passing it in (an `array_smooth_1d` primitive can land separately when needed).

### audio_waveform_path

`audio_waveform_path(envelope, width, height, fill Color)` → Drawing

Builds a **single closed filled** path tracing the envelope's outline: top edge (max samples) left-to-right, then bottom edge (min samples) right-to-left, then closed. The natural primitive for the smooth filled-curve waveform aesthetic — distinct from the per-column-bars look produced by `audio_waveform_drawing`.

Coordinates: column `b` sits at `x = b * (width - 1) / (bins - 1)`; each amplitude `a` maps to `y = height/2 - a * height/2`, so the centreline is at `height/2`, `+1` lands at the top edge, `-1` at the bottom. The closed path composites cleanly onto any background.

```sql
-- Smooth filled silhouette over a dark canvas
SELECT render(
    draw_group([
        draw_rect(point2d(0, 0), point2d(1200, 240), color_hex('#0d1117')),
        audio_waveform_path(audio_waveform_envelope(clip, 400), 1200, 240,
                            color_hex('#7fdbff80'))
    ]),
    point2d(1200, 240)) AS hero
FROM clips
```

Width and height must be positive. Non-`(bins, 2)`-shaped envelopes raise. For a stroked outline instead of a fill, compose the same envelope through `audio_waveform_drawing` with a per-column line lambda — or stack a filled path under stroked bars for a hybrid look.

### audio_waveform

`audio_waveform(audio, width, height, options)` → Image

One-liner sugar over the envelope + per-column rendering stack: decodes the audio, builds a per-pixel-column peak envelope (`bins = width`), and rasterises the classic Audacity-style vertical-bars waveform onto a solid background. Use this when you just want the picture; reach for the lower-level functions when you want gradients, smoothing, custom styles, or rich composition.

The **`options` struct** carries two required `Color` fields:

- `fg` — waveform stroke colour.
- `bg` — background fill colour.

Field order doesn't matter; the function resolves names via the runtime type descriptor.

```sql
-- Hero image for a dataset card
SELECT audio_waveform(clip, 1200, 240,
    {fg: color_hex('#7fdbff'), bg: color_hex('#0d1117')}) AS hero
FROM clips
```

```sql
-- Inline preview thumbnails for a search result
SELECT path,
       audio_waveform(clip, 400, 60,
           {fg: color(255, 200, 50), bg: color(20, 20, 30)}) AS thumb
FROM clips
```

Width and height must be positive. Null audio, null `width`, null `height`, or null `options` produces a null Image. An `options` struct missing `fg` or `bg` raises an error pointing at the actual field names found. Each column is rendered as a 1-pixel-wide vertical line from the bin minimum to the bin maximum.

## See Also

- [CREATE MODEL](../sql/create-model.md) — the DDL surface that wires `audio_samples` / `audio_to_log_mel` into ONNX speech / audio pipelines.
- [Inference Helpers](inference.md) — sequence-to-sequence dispatch (`decode_seq2seq`) used by Whisper-style decoders that consume `audio_to_log_mel` features.
- [Tokenization Functions](tokenization.md) — `tokenizer.decode_bpe` and `tokenizer.byte_level_decode` for the text-side of speech-to-text bodies.
- [Drawing Functions](drawing.md) — `render`, drawing primitives, and the broader procedural-visual toolkit that `audio_waveform_drawing` / `audio_waveform_path` slot into.
- [Vector & Tensor Functions](vector.md) — operations on tensors produced by `audio_samples`.
- [Functions Reference](string.md) — complete function listing across all categories.
