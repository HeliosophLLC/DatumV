# LJSpeech

A small, single-speaker English speech corpus released in 2017 by Keith Ito.
Roughly 24 hours of a US English female voice reading short passages from
seven non-fiction LibriVox audiobooks. Despite its age and modest size, it
remains the default reach-for dataset whenever someone wants to prototype a
TTS model, sanity-check a vocoder, or — as here — exercise an audio
ingestion pipeline end-to-end without committing to a multi-hundred-GB
download.

The audio is the cleanest you'll find in an openly-licensed corpus: a single
speaker, a single recording environment, consistent 22.05 kHz mono 16-bit
WAV, and short clips (~6 seconds median). No code-switching, no overlap, no
channel-mismatch surprises.

## When to use it

| Goal                                          | Notes                                                              |
| --------------------------------------------- | ------------------------------------------------------------------ |
| **Validate audio ingestion**                  | Smallest "real" speech corpus. ~3.2 GB compressed (gzip mirror), ~3.8 GB extracted. |
| Prototype single-speaker TTS                  | The canonical training set. Pair with the (deferred) transcript table. |
| Train a vocoder                               | Clean studio-quality input; converges in hours.                    |
| Smoke-test a pretrained ASR (Whisper, etc.)   | Single voice → easy listening test for transcription quality.      |

Reach for **LibriSpeech** instead if you need multi-speaker variation, noisy
conditions, or a published WER benchmark.

## Example SQL

Probe the first few clips:

```sql
SELECT file_name, file_duration_ms / 1000.0 AS seconds
FROM datasets.ljspeech_audio
ORDER BY file_name
LIMIT 5;
```

Histogram of clip durations:

```sql
SELECT
  (file_duration_ms / 1000)::Int32 AS duration_sec,
  COUNT(*) AS clips
FROM datasets.ljspeech_audio
GROUP BY duration_sec
ORDER BY duration_sec;
```

Transcribe a sample with a pretrained ASR model:

```sql
SELECT file_name, models.whisper_tiny(file) AS transcript
FROM datasets.ljspeech_audio
LIMIT 20;
```

## Output schema

```
file_name:          String      -- entry path inside the source tar
file:               Audio       -- decoded WAV, sidecar-backed
file_sample_rate:   Int32       -- 22050 for every clip
file_channels:      UInt8       -- 1 (mono)
file_bit_depth:     UInt8       -- 16
file_duration_ms:   Int64       -- millisecond duration parsed from the WAV header
file_byte_length:   Int64       -- uncompressed byte size of the WAV
```

## Tips

- **Sidecar-backed audio** — the `file` column carries a handle into a
  `.datum-blob` companion; the WAV bytes are not inlined. Most metadata
  queries (counts, duration histograms) never touch the blob store.
- **No transcripts in this variant** — LJSpeech's archive includes a
  `metadata.csv` mapping clip id → original / normalized text. The current
  variant skips it; adding a transcript-paired variant is a follow-up once
  the CSV-with-pipe-delimiter loader is wired up.
- **All clips share the same recording chain** — useful for any model
  sensitive to channel statistics. Unrealistic if your downstream task
  involves microphone variation; train on a multi-recording corpus instead.

## License & attribution

The LJ Speech Dataset is in the public domain. The texts (published
1884–1964) are out of copyright in the United States; the audio was
recorded by LibriVox volunteers and dedicated to the public domain by
Keith Ito.

- Site: [keithito.com/LJ-Speech-Dataset](https://keithito.com/LJ-Speech-Dataset/)
- Mirror: [data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2](https://data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2)
