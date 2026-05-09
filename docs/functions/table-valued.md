---
title: Table-Valued Functions
category: table
---

# Table-Valued Functions

[< Back to Functions Reference](string.md) | [SQL Reference](../sql/select.md)

Table-valued functions produce multiple rows and are used in FROM, CROSS JOIN, and LATERAL JOIN clauses. When used with `CROSS JOIN LATERAL` or `CROSS APPLY`, the function arguments can reference columns from the left-hand table, enabling per-row expansion of array or nested data.

### unnest

`unnest(array_col)` -> Rows | QU: 1

Expand array-valued column into separate rows. Works with Vector, UInt8Array, JsonValue arrays.

```sql
-- Expand a vector column per row using lateral join
SELECT t.name, s.value
FROM data AS t
CROSS JOIN LATERAL UNNEST(t.scores) AS s
```

### range

`range(start, end[, step])` -> Rows | QU: 1

Generate a sequence of rows with a `Value` column from start to end (inclusive). Default step is 1.

See [SQL Reference -- LATERAL JOIN / APPLY](../sql/joins.md#lateral-join--apply) for full syntax and examples.

### video_unnest_frames

`video_unnest_frames(source [, start_frame [, stride [, max_frames]]])` -> Rows | QU: 1

Enumerates frames of a video as lazy `VideoFrame` handles. Each output row is `(frame_index Int32, frame VideoFrame)`. The function does no decoding — it opens the source once to read container metadata, then emits one handle per frame in stride order. Pixels are materialised only when a downstream consumer (typically `video_frame_to_image`) routes the handle back through the per-query video registry.

`source` is either a STRING file path or a `Video` column value. Sidecar-backed Video columns are read directly from the `.datum-blob` window via a seekable stream — no full-file copy. Arena-backed Video values are read into a managed buffer once and fed to FFmpeg via an in-memory stream.

`start_frame` defaults to 0; `stride` to 1; `max_frames` defaults to the container's reported frame count (when known — falls back to "all frames" semantics where the function emits handles until the source returns end-of-stream).

```sql
-- All frames of a stored video, decoded at source resolution
SELECT video_frame_to_image(f.frame) AS img
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f

-- Every 10th frame from frame 100 onward, capped at 30 rows
SELECT video_frame_to_image(f.frame, 384) AS img
FROM video_unnest_frames('clip.mp4', 100, 10, 30) AS f

-- Per-frame depth estimation, false-coloured
SELECT f.frame_index,
       apply_colormap(models.midas_small(video_frame_to_image(f.frame, 384)), 'turbo') AS depth_viz
FROM videos AS v
CROSS APPLY video_unnest_frames(v.video) AS f
```

Sequential frame access (frame N → N+1 → N+2) hits a warm FFmpeg decoder; backward access seeks to the file head and decodes forward. Stay in `frame_index` order whenever possible.

See [Examples — Video frames as a queryable column](../sql/examples.md#video-frames-as-a-queryable-column) for full pipelines.

### open_archive

`open_archive(source [, path_pattern])` -> Rows | QU: 1

Opens a ZIP / TAR / TAR.GZ / TAR.BZ2 archive and yields one row per regular-file entry. Streams N rows per batch with the body bytes materialized into the query arena — no temp extraction, no managed `byte[]` per entry. The load-bearing primitive behind SQL dataset recipes: compose with `read_csv`, `audio_decode`, `image_decode`, joins, and CTAS to shape raw archives into typed tables.

Output columns: `(path STRING, size INT64, modified TIMESTAMPTZ, bytes Array<UInt8>)`. `path` is the archive-relative entry path with forward slashes; `size` is the uncompressed length; `modified` carries the entry's container-recorded mtime (NULL when the format omits it); `bytes` is the raw uncompressed payload as an arena-resident byte array.

`path_pattern` is an optional SQL LIKE pattern (`%` matches zero-or-more chars, `_` matches one) applied **before body decompression**. Entries whose path doesn't match are skipped without reading their bytes — material for recipes that walk the same archive twice (transcripts + media). When the argument is omitted the default `'%'` matches every entry.

OS / editor metadata entries (`__MACOSX/`, `.DS_Store`, `thumbs.db`, `desktop.ini`, leading-dot files) are returned in the row stream — this is the deliberate raw-scan contract. Recipes that want them dropped add `WHERE path NOT LIKE '\_\_MACOSX/%'` etc. The homogeneous-media-bag ingest pipeline applies a metadata filter at its own layer; this TVF does not.

Compression wrappers (`.gz`, `.bz2`) are unwrapped transparently by the underlying file descriptor — `LJSpeech-1.1.tar.bz2` and `dev-clean.tar.gz` route through the same code path as a plain `.tar`. The unwrap currently pre-materializes the decompressed payload to a temp file on first open, so first-touch on a multi-GB `.bz2` source is dominated by single-threaded bz2 decompression — minutes, not seconds. Re-encoding to `.tar.gz` on the source side avoids the cost.

```sql
-- All entries in an archive, one row per file
SELECT path, size, modified
FROM open_archive('LJSpeech-1.1.tar.gz')

-- Filter at the read boundary — non-matching entries' bytes are never decompressed
SELECT path, bytes
FROM open_archive('LibriSpeech.tar.gz', path_pattern := '%.flac')

-- Pull a specific entry's bytes out for inline parsing
SELECT bytes
FROM open_archive('LJSpeech-1.1.tar.gz')
WHERE path = 'LJSpeech-1.1/metadata.csv'
LIMIT 1

-- LJSpeech: join the manifest CSV (inside the archive) to the WAV entries
WITH manifest AS (
  SELECT
    fields[1] AS clip_id,
    fields[2] AS transcript,
    fields[3] AS normalized_transcript
  FROM read_csv(
    (SELECT bytes FROM open_archive('LJSpeech-1.1.tar.gz')
     WHERE path = 'LJSpeech-1.1/metadata.csv' LIMIT 1),
    delimiter := '|'
  )
)
SELECT m.clip_id, m.transcript, audio_decode(o.bytes) AS audio
FROM manifest m
JOIN open_archive('LJSpeech-1.1.tar.gz', path_pattern := 'LJSpeech-1.1/wavs/%.wav') o
  ON o.path = 'LJSpeech-1.1/wavs/' || m.clip_id || '.wav'
```

### open_folder

`open_folder(source [, recursion_depth [, path_pattern]])` -> Rows | QU: 1

Walks a filesystem directory and yields one row per regular file. The on-disk analogue of `open_archive` — same output schema (`path STRING, size INT64, modified TIMESTAMPTZ, bytes Array<UInt8>`), same streaming-into-arena memory shape, same path-pattern filter — so recipes built for archive sources port to directory sources by swapping the call. Use when the source dataset is already extracted, a scratch directory of in-progress media, or a drop folder being staged for ingest.

`path` is **relative to `source`** and forward-slashed (`sub/file.txt`, not `C:\…\sub\file.txt`). This is what makes the call swappable with `open_archive` in joins — the same `WHERE path = 'wavs/' || id || '.wav'` predicate works against either source kind.

`recursion_depth` defaults to `0` (top-level files only — direct children of `source`, no descent). `N > 0` walks `N` levels of subdirectories. `-1` is unlimited. The depth check happens at directory traversal time, before any file is opened, so a shallow recursion on a deep tree skips untouched-subtree IO entirely. Negative values other than `-1` throw — anything else is almost certainly a bug.

`path_pattern` mirrors `open_archive`: a SQL LIKE pattern applied to the relative path before reading file bytes. Use to prune the row stream when recursion would find more than the recipe wants.

Permissions errors and races (a subdirectory deleted mid-walk, an ACL-denied entry) surface as "no rows from that branch" rather than aborting the whole call. Files held with no-share semantics by other processes — the canonical case is `C:\DumpStack.log.tmp` and `pagefile.sys` on Windows, but also live database files and in-progress writes — are silently skipped at the file-open boundary for the same reason: walking `C:\` shouldn't abort on the first kernel-locked file. The TVF opens with `FileShare.ReadWrite | FileShare.Delete` so it coexists with apps actively writing to the directory. Leading-dot files, `__MACOSX/`, `thumbs.db`, etc. are returned — raw-scan contract, filter via SQL.

```sql
-- Direct files of a single directory, ignoring subdirectories
SELECT path, size FROM open_folder('C:\datasets\ljspeech\wavs')

-- Full recursive walk of a downloaded-and-extracted LibriSpeech split
SELECT path, audio_decode(bytes) AS clip
FROM open_folder('D:\corpora\LibriSpeech\dev-clean',
                 recursion_depth := -1,
                 path_pattern := '%.flac')

-- One level deep, e.g. a top-level metadata.csv plus the immediate "wavs/" subtree
SELECT path, size
FROM open_folder('D:\corpora\LJSpeech-1.1', recursion_depth := 1)

-- Recipe portability — same shape as open_archive, against an extracted tree
WITH manifest AS (
  SELECT fields[1] AS clip_id, fields[2] AS transcript
  FROM read_csv(
    (SELECT bytes FROM open_folder('D:\corpora\LJSpeech-1.1')
     WHERE path = 'metadata.csv' LIMIT 1),
    delimiter := '|'
  )
)
SELECT m.clip_id, m.transcript, audio_decode(o.bytes) AS audio
FROM manifest m
JOIN open_folder('D:\corpora\LJSpeech-1.1',
                 recursion_depth := 1,
                 path_pattern := 'wavs/%.wav') o
  ON o.path = 'wavs/' || m.clip_id || '.wav'
```

### list_folder

`list_folder(source [, recursion_depth [, path_pattern]])` -> Rows | QU: 1

Yields one row per regular file under a filesystem directory with metadata only — `path`, `size`, `modified`, no bytes. The no-body counterpart to `open_folder`. Use for listings, file-count queries, size audits, finding the biggest files, or generating a manifest to feed into a subsequent `open_folder` + `JOIN`. Cheap: no file open, no arena pressure, no IO beyond the directory enumeration itself — a recursive walk of hundreds of thousands of files completes in seconds.

Signature semantics are identical to `open_folder`: same `recursion_depth` (`0` default, `-1` unlimited, `N` for N levels), same `path_pattern` SQL LIKE filter, same forward-slashed relative-to-source `path` column. The whole point is that recipes can swap one for the other based on whether they need bytes.

**Key difference from `open_folder`**: kernel-locked files (`DumpStack.log.tmp`, `pagefile.sys`, `hiberfil.sys`) and ACL-denied files **still appear in the row stream** because no file body is opened — size and modification time live in the directory entry, which is readable for these files. `open_folder` skips them silently because it can't read their bytes; `list_folder` shows you they exist. If you want to filter them out, use `WHERE NOT path LIKE 'DumpStack%'` etc.

```sql
-- How many files of each extension under a tree?
SELECT regexp_extract(path, '\.([^.]+)$', 1) AS ext, COUNT(*) AS files
FROM list_folder('C:\datasets', recursion_depth := -1)
GROUP BY ext
ORDER BY files DESC

-- Biggest files in a downloads directory
SELECT path, size FROM list_folder('C:\Downloads')
ORDER BY size DESC
LIMIT 20

-- Two-phase recipe: list first, then selectively open the matching subset
WITH targets AS (
  SELECT path FROM list_folder('D:\corpora', recursion_depth := -1)
  WHERE path LIKE '%.flac' AND size > 50000
)
SELECT t.path, audio_decode(o.bytes) AS clip
FROM targets t
JOIN open_folder('D:\corpora', recursion_depth := -1, path_pattern := '%.flac') o
  ON o.path = t.path
```

### read_csv

`read_csv(bytes [, delimiter])` -> Rows | QU: 1

Parses CSV bytes into rows of `Array<String>` — each input line becomes one row whose single `fields` column carries the split field values. Designed for composition with `open_archive` / `open_folder` so SQL recipes can read a manifest from inside an archive (or a sibling `metadata.csv` on disk) without ever touching the bytes from a separate file path.

Output column: `fields Array<String>`. Project named columns positionally — `fields[1]` is the first field, `fields[2]` the second, etc. The 1-based indexing is the engine's array convention; combine with `AS` aliases for readable downstream SQL:

```sql
SELECT fields[1] AS clip_id, fields[2] AS transcript FROM read_csv(...)
```

**Why `Array<String>` rather than named columns.** Named-column output would require the planner to know the column count and names at plan time, but for a runtime-bound `bytes` argument neither is available until execute. Returning `Array<String>` sidesteps a planner enhancement (constant-fold-at-validate for TVF arguments) and lets recipes ship today. A schema-bearing overload can be added alongside this one once the planner grows that hook — existing recipes continue to work unchanged.

`delimiter` is an optional single-character STRING (`,` by default). Common values: `','` for CSV, `'\t'` for TSV, `'|'` for LJSpeech-style pipe-delimited manifests. Multi-character delimiters throw — composite separators aren't supported in v1.

**Parser scope (v1).** Simple split by delimiter — no RFC 4180 quoting, no embedded-newline support inside quoted fields, no escape handling. Most flat manifests (LJSpeech `metadata.csv`, Common Voice `train.tsv`, AudioSet TSVs) work fine. CSV payloads with `"`-quoted fields containing embedded delimiters won't split correctly until a richer parser lands; route those through the file-path CSV ingest path instead.

Line endings: `\n` is the separator; trailing `\r` on each line is stripped (`\r\n` payloads parse cleanly). Empty fields between delimiters are preserved (`a,,c` yields `["a", "", "c"]`). NULL bytes input yields no rows.

```sql
-- LJSpeech-shaped pipe-delimited manifest
SELECT fields[1] AS clip_id, fields[2] AS transcript
FROM read_csv(:manifest_bytes, delimiter := '|')

-- TSV with header (caller filters the header line via WHERE)
SELECT fields[1] AS client_id, fields[2] AS path, fields[3] AS sentence
FROM read_csv(:tsv_bytes, delimiter := '\t')
WHERE fields[1] != 'client_id'
```

## See Also

- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [Window Functions](window.md) -- per-row computations over partitions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
