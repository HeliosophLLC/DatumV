-- LibriSpeech utterances table — joins per-utterance FLAC audio with the
-- sentence-level transcript that ships in colocated `.trans.txt` files.
-- Used unchanged by every LibriSpeech variant (dev-clean / dev-other /
-- test-clean / test-other / train-clean-100 / train-clean-360 /
-- train-other-500) because all splits share the same archive layout.
--
-- Archive layout (every split untars under a top-level `LibriSpeech/`):
--   LibriSpeech/<split>/<speaker>/<chapter>/<speaker>-<chapter>-<utt>.flac
--   LibriSpeech/<split>/<speaker>/<chapter>/<speaker>-<chapter>.trans.txt
--   LibriSpeech/{LICENSE,README,CHAPTERS,SPEAKERS,BOOKS}.TXT
--
-- The top-level metadata `.TXT` files are why the media-bag path rejects
-- this tarball (mixed content). The LIKE filters below pick only the FLAC
-- and per-chapter `.trans.txt` entries, so the metadata files are skipped
-- without decompressing their bodies.
--
-- `.trans.txt` line format is `<utt-id> <text>` — single space between
-- the id and the rest. Transcripts are uppercase ASCII (letters, spaces,
-- apostrophes); commas never appear, so read_csv's default ',' delimiter
-- delivers the whole line as fields[1] and we split id vs. text with
-- regexp_extract.
WITH transcripts AS (
    SELECT
        regexp_extract(f.fields[1], '^(\S+)', 1) AS utt_id,
        regexp_extract(f.fields[1], '^\S+\s+(.*)$', 1) AS transcript
    FROM open_archive($artifact, '%.trans.txt') AS tt
    JOIN read_csv(tt.bytes) AS f
    WHERE f.fields[1] <> ''
),
clips AS (
    SELECT
        -- 'LibriSpeech/dev-clean/1272/128104/1272-128104-0000.flac'
        -- → '1272-128104-0000'
        regexp_replace(path, '^.*/([^/]+)\.flac$', '\1') AS utt_id,
        audio_decode(bytes) AS clip
    FROM open_archive($artifact, '%.flac')
)
SELECT
    c.utt_id,
    c.clip,
    t.transcript
FROM clips AS c
JOIN transcripts AS t ON t.utt_id = c.utt_id
