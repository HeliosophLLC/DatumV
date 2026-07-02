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
SELECT utt_id, clip, audio_duration(clip) AS seconds
FROM datasets.ljspeech_audio
ORDER BY utt_id
LIMIT 5;
```

Histogram of clip durations:

```sql
SELECT
  CAST(audio_duration(clip) AS Int32) AS duration_sec,
  COUNT(*) AS clips
FROM datasets.ljspeech_audio
GROUP BY duration_sec
ORDER BY duration_sec;
```

Transcribe a sample with a pretrained ASR model:

```sql
SELECT utt_id, clip, models.whisper_tiny(clip) AS transcript
FROM datasets.ljspeech_audio
LIMIT 20;
```

## Output schema

```
utt_id:      String      -- utterance id (e.g. 'LJ001-0001'), from the WAV filename stem
clip:        Audio       -- decoded WAV, sidecar-backed
file_bytes:  Int64       -- WAV payload size in the source archive
modified:    Timestamp   -- archive entry mtime
```

The source WAVs are 22.05 kHz mono 16-bit; use `audio_duration(clip)`
(seconds) for length rather than a stored column. Audio decodes to 16 kHz
mono inside model bodies that need it (e.g. Whisper, Silero VAD).

## Tips

- **Sidecar-backed audio** — the `clip` column carries a handle into a
  `.datum-blob` companion; the WAV bytes are not inlined. Most metadata
  queries (counts by id, byte-size stats) never touch the blob store.
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
