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

`frame_index` is **0-based** — frame 0 is the first frame of the container. This is the FFmpeg / ffprobe / video-tooling convention and deliberately departs from PostgreSQL's 1-based ordinality (`generate_series`, `WITH ORDINALITY`, array subscripts): aligning with PG would force `start_frame` and `max_frames` to disagree with every external video tool the user compares against. If a recipe wants a 1-based row number that's distinct from the underlying frame address, layer `ROW_NUMBER() OVER ()` over the output.

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

**Parser scope.** RFC 4180 quoted fields are honoured: wrapping `"..."` is stripped, embedded `""` collapses to a single `"`, and delimiters inside quotes are preserved (`"a,b","c"` → `["a,b", "c"]`). Embedded newlines inside quoted fields are **not** supported — the surrounding loop breaks the payload on bare `\n` before quote state is considered, so a multi-line cell arrives broken into parts. For multi-line CSV cells, reach for the typed file-path ingest path.

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

### open_csv

`open_csv(path [, delimiter])` -> Rows | QU: 1

Streams a CSV file from disk and yields one row per line, each row carrying a single `fields Array<String>` column. The file-path analogue of `read_csv` — same single-column output, same positional projection pattern, same line-splitter — for the case where the manifest is a loose file rather than bytes inside an archive. Avoids the byte-materialisation hop and stays bounded regardless of file size.

Output column: `fields Array<String>`. Project named columns positionally — `fields[1]` is the first field, `fields[2]` the second, etc. — combine with `AS` aliases for readable downstream SQL.

`delimiter` is an optional single-character STRING (`,` by default). Multi-character delimiters throw.

**Parser scope.** Same as `read_csv`: RFC 4180 quoting handled, embedded newlines inside quoted fields not. Recipes that work against `read_csv` swap to `open_csv` without touching downstream projections.

Line endings: `\n` is the separator; trailing `\r` is stripped (`\r\n` payloads parse cleanly). Empty interior fields are preserved. UTF-8 decoding is assumed; non-UTF-8 payloads come through with garbled characters rather than throwing.

The file is opened with `FileShare.ReadWrite | FileShare.Delete` so the reader coexists with a manifest being appended to or rotated.

```sql
-- Pipe-delimited LJSpeech manifest sitting next to an extracted dataset
SELECT fields[1] AS clip_id, fields[2] AS transcript
FROM open_csv('D:\corpora\LJSpeech-1.1\metadata.csv', delimiter := '|')

-- TSV with header (caller filters the header line via WHERE)
SELECT fields[1] AS client_id, fields[2] AS path
FROM open_csv('C:\datasets\common_voice\train.tsv', delimiter := '\t')
WHERE fields[1] != 'client_id'
```

### open_csv_typed

`open_csv_typed(path)` -> Rows

Opens a CSV file with **plan-time type inference** and yields typed, named rows — the schema-bearing sibling of `open_csv`. Where `open_csv` returns a single `fields Array<String>` column and leaves projection positional, `open_csv_typed` runs the ingest-grade CSV scanner at plan time, builds a real per-column schema (narrowed integers, dates, leading-zero codes preserved as strings, etc.), and surfaces the columns by their CSV header names.

**Plan-time scan.** `path` must be a constant STRING at plan time (literal in source, or a `$parameter` reference the parameter binder has substituted). The validator runs a full file pass — the same authoritative inference path the dataset ingest pipeline uses — and builds a real `Schema`, so `SELECT date, primary_type FROM open_csv_typed(...)` type-checks against the actual CSV columns. Calling with a non-constant argument throws — recipe writers must inline the path or pass it as a bound parameter.

The scan result is cached for the subsequent execute pass so the actual query doesn't pay twice. For interactive `LIMIT 5` exploration on multi-GB CSVs the scan still dominates wall time — reach for `open_csv` instead if you only want a quick look without per-column types.

**Uncompressed only.** Compressed CSVs (`.csv.gz`, `.csv.bz2`) are not supported: scanning a compressed file at plan time would require full decompression-to-temp on the planner thread, which doesn't match the cheap-plan-time contract `ValidateArguments` is expected to honour. Decompress to a `.csv` first and pass the uncompressed path.

```sql
-- Real typed columns, no positional indexing
SELECT clip_id, transcript
FROM open_csv_typed('D:\corpora\LJSpeech-1.1\metadata.csv');

-- Chicago crimes: the typed schema includes a real TIMESTAMPTZ for the date column,
-- so date arithmetic works without per-row casting
SELECT "Primary Type", COUNT(*) AS n
FROM open_csv_typed('D:\datasets\chicago\Crimes_-_2001_to_Present.csv')
WHERE EXTRACT(year FROM "Date") = 2024
GROUP BY "Primary Type"
ORDER BY n DESC;

-- Bound parameter (substituted to a literal at plan time)
SELECT *
FROM open_csv_typed($manifest_path);
```

### open_json

`open_json(path)` -> Rows

Opens a JSON file with **plan-time type inference** and yields typed, named rows. Accepts two root shapes — a single object (1 row) or an array of objects (N rows) — and rejects primitive or array-of-non-object roots. The schema is the union of keys across rows with per-column kinds inferred from observed values.

**Plan-time scan.** `path` must be a constant STRING at plan time (literal in source, or a `$parameter` reference the parameter binder has substituted). The validator parses the document, runs the shared JSON type scanner over every row, and builds a real `Schema` — so `SELECT id, name FROM open_json(...)` type-checks against the actual keys. Calling with a non-constant argument throws — inline the path or pass it as a bound parameter.

**Type inference.** Each column's kind is the most-specific family that holds for every non-null value: integers narrow to the smallest fitting width (`UInt8`, `Int16`, …, `Int64`, `UInt128`, `Int128`); fractional numbers narrow to `Float32` when every value round-trips through single precision, else `Float64`; pure JSON-`true`/`false` columns become `Boolean`; pure JSON-string columns become `String`. Columns whose values mix scalar families across rows, or hold nested objects / arrays, fall through to `Json` so the original shape stays queryable.

**Nullability.** JSON `null` and key absence are treated identically — a column present in some rows and missing in others becomes nullable.

**Memory.** The document is parsed in full once at plan time (scan) and again at execute time (emit), so peak memory is one document's worth of parsed tape. For multi-GB feeds prefer `open_jsonl`, which streams line-by-line.

Compressed inputs (`.json.gz`, `.json.bz2`) route through the same descriptor as the file-format ingest path, so a gzipped JSON file works without manual decompression.

```sql
-- Typed columns straight from a root-array document
SELECT id, name, score
FROM open_json('D:\datasets\users.json');

-- Nested values land as Json — query them with the json_* function family
SELECT id, json_extract_path(metadata, 'tags', 0) AS first_tag
FROM open_json('D:\datasets\events.json');

-- Bound parameter (substituted to a literal at plan time)
SELECT *
FROM open_json($feed_path);
```

### open_jsonl

`open_jsonl(path)` -> Rows

Opens a newline-delimited JSON file (`.jsonl` / `.ndjson`) with **plan-time type inference** and yields one row per non-empty line. Each line is one independent JSON value. The file's shape is locked on the first non-empty line:

- If line 1 is a JSON object → **object-mode**. Schema is the union of keys across all lines with narrowed per-column kinds (same inference as `open_json`).
- If line 1 is any other JSON value (array, string, number, true/false, null) → **single-column-mode**. Schema is a single `value Json` column; each line lands as one `Json` cell.

Mid-file shape mismatches (an object line in a single-column-mode file, or a non-object line in an object-mode file) throw with the offending line number. Empty / whitespace-only lines are skipped.

**Plan-time scan.** `path` must be a constant STRING at plan time. The validator walks the file once to detect shape and infer the schema — same authoritative inference path the JSONL ingester uses. Calling with a non-constant argument throws.

**Streaming.** Both the scan pass and the emit pass walk lines one at a time, parsing each as its own JSON document and disposing it before reading the next. Peak memory stays bounded to a single line's parsed shape regardless of file size — preferred over `open_json` for large feeds.

```sql
-- Typed columns from an object-mode JSONL log
SELECT timestamp, user_id, event
FROM open_jsonl('D:\logs\events.jsonl')
WHERE event = 'login';

-- Single-column-mode: each line is a JSON array, kept whole
SELECT json_array_length(value) AS row_width
FROM open_jsonl('D:\datasets\rows.jsonl');

-- Bound parameter (substituted to a literal at plan time)
SELECT *
FROM open_jsonl($feed_path);
```

### open_fits_hdus

`open_fits_hdus(path)` -> Rows

Opens a FITS file and yields **one row per Header-Data Unit (HDU)** with its parsed metadata. The interrogation TVF — call this first to see what's inside an unfamiliar FITS file before deciding whether to pull pixel data with `open_fits_images` or table rows with `open_fits_table`.

A FITS file is a sequence of HDUs. Each HDU has a header section (2880-byte blocks of 80-char ASCII "cards") describing its contents, followed by an optional data section. The data can be one of three shapes: an N-dimensional **image** (pixel array), a **binary table** (typed rows × columns), or an **ASCII table** (rare). A single file commonly mixes multiple kinds — a JWST L2 product, for example, might bundle a header-only primary HDU plus `SCI`/`ERR`/`DQ` image extensions plus a binary-table extension with per-pixel variance.

Output columns:

| Column | Type | Notes |
|---|---|---|
| `hdu_index` | `INT64` | 0 = primary, 1+ = extensions |
| `kind` | `STRING` | `'primary'`, `'image'`, `'bintable'`, `'asciitable'`, or `'unknown'` |
| `extname` | `STRING?` | `EXTNAME` card value, e.g. `'SCI'`, `'ERR'`, `'CATALOG'` |
| `extver` | `INT32?` | `EXTVER` card value |
| `bitpix` | `INT32?` | Pixel format selector: `8/16/32/64` (integer), `-32/-64` (IEEE float) |
| `naxis` | `INT32` | Dimension count (`0` for header-only HDUs) |
| `naxisn` | `INT32[]` | `[NAXIS1, NAXIS2, …]` — fast-axis first |
| `nrows` | `INT64?` | `NAXIS2` for `bintable` HDUs; `NULL` for image HDUs |
| `ncols` | `INT32?` | `TFIELDS` for `bintable` HDUs; `NULL` for image HDUs |
| `header` | `JSON` | Full card list as an array of `{key, value, comment}` objects, file order |

Transparent `.fits.gz` support: pass the gzipped path directly and the file is decompressed in memory.

The `header` column is the doorway to the rich, instrument-specific metadata that doesn't get its own typed column — observation IDs, filter names, exposure times, WCS keywords, telescope identification, processing history. Query it with JSON path operators just like any other JSON-valued column.

```sql
-- Survey a file: what kinds of HDUs does it contain?
SELECT hdu_index, kind, extname, bitpix, naxisn, nrows, ncols
FROM open_fits_hdus('/data/jw01234.fits');

-- Pull a specific keyword's value out of the header for every HDU
SELECT
    hdu_index,
    extname,
    (SELECT card->>'value' FROM jsonb_array_elements(header) AS card
     WHERE card->>'key' = 'EXPTIME') AS exposure_seconds
FROM open_fits_hdus('/data/jw01234.fits');
```

### open_fits_images

`open_fits_images(path)` -> Rows

Opens a FITS file and yields **one row per image HDU**, surfacing both a displayable PNG preview and the scientific Float32 pixel array. Binary-table and header-only HDUs are skipped — they have no image to project.

Output columns:

| Column | Type | Notes |
|---|---|---|
| `hdu_index` | `INT64` | Source HDU index (preserved across skipped non-image HDUs) |
| `extname` | `STRING?` | `EXTNAME` card value |
| `image` | `IMAGE?` | PNG-encoded grayscale preview, min/max-stretched. Populated only when `NAXIS == 2`. NULL for 1-D spectra and higher-rank cubes. |
| `sci` | `FLOAT32[]?` | Per-pixel scientific value with BSCALE/BZERO applied. Populated whenever the HDU has pixel data. For NAXIS ≥ 2 the cell is a multi-dim `FLOAT32` with shape `[NAXISn, …, NAXIS2, NAXIS1]` (slowest-axis-first, NumPy convention) — so `sci[y, x]` indexes a 2-D pixel directly, `sci[z, y, x]` indexes a 3-D spectral cube voxel, etc. For NAXIS = 1 (1-D spectrum) the cell is flat. |
| `header` | `JSON` | Full card list, same shape as `open_fits_hdus` |

**`image` vs `sci`.** Two columns, two audiences. The `image` column is a browser-displayable thumbnail — handy for chat UIs, dataset previews, sanity checks. The `sci` column carries the *actual* per-pixel scientific values in physical units (BSCALE/BZERO applied during decode), suitable for SQL math, NaN masks, per-pixel filtering, statistics. A query that just wants to look at a frame reads `image`; a query that wants to compute on the pixels reads `sci`.

**BITPIX decode (v1).** Each pixel is read big-endian per the FITS standard, then linearly rescaled to Float32 as `physical = BZERO + BSCALE * raw`:

| BITPIX | On-disk format |
|---|---|
| `8` | unsigned 8-bit integer |
| `16` | signed 16-bit integer |
| `32` | signed 32-bit integer |
| `64` | signed 64-bit integer |
| `-32` | IEEE 754 single-precision float |
| `-64` | IEEE 754 double-precision float |

NaN pixels become `0` (black) in the displayable preview; the `sci` column carries them through verbatim so per-pixel masking still works.

```sql
-- Browse the image HDUs in a file
SELECT hdu_index, extname, image FROM open_fits_images('/data/jw01234.fits');

-- Compute a per-frame median (assumes sci is reshapable from NAXIS)
SELECT extname, array_median(sci) AS median_flux
FROM open_fits_images('/data/jw01234.fits')
WHERE sci IS NOT NULL;
```

### open_fits_table

`open_fits_table(path, ext)` -> Rows

Opens a FITS **binary-table HDU** and yields its rows with one output column per declared `TTYPEn`/`TFORMn` — typed, named columns matching what the file itself says it contains. Use this for catalogs (DESI redshift tables, SDSS source catalogs), event lists (Chandra X-ray events), and time-series data.

`ext` selects which HDU to read:
- **STRING** — case-insensitive match against the `EXTNAME` card (recommended; recipes that key on EXTNAME survive file-layout changes).
- **INT64** — HDU index, where 0 is the primary HDU. Throws if the indexed HDU isn't a binary table.

**Plan-time schema peek.** Both arguments must be constants at plan time (literals in source, or `$parameter` references that the parameter binder has substituted). The validator opens the file, walks to the target HDU, and reads the `TTYPEn`/`TFORMn` cards to produce a real `Schema` — so `SELECT TARGETID, RA, DEC FROM open_fits_table(...)` type-checks against the actual catalog columns. Calling with a non-constant argument (e.g. a column reference inside a JOIN) throws — recipe writers must inline the path/ext or pass them as bound parameters.

**Supported `TFORM` types (v1).**

| TFORM | DataKind | Notes |
|---|---|---|
| `L` | `BOOLEAN` | logical |
| `B` | `UINT8` | byte |
| `I` | `INT16` | big-endian |
| `J` | `INT32` | big-endian |
| `K` | `INT64` | big-endian |
| `E` | `FLOAT32` | big-endian |
| `D` | `FLOAT64` | big-endian |
| `A`*(n)* | `STRING` | fixed-width ASCII, right-trimmed of spaces |
| *r*`E` / *r*`J` / *r*`I` / ... | `FLOAT32[]` / `INT32[]` / `INT16[]` / ... | `repeat > 1` numeric forms emit a typed array column |

Variable-length array columns (`TFORM` `P` and `Q`), complex (`C`, `M`), and bit arrays (`X`) throw `NotSupportedException` — catalogs that need them land via a follow-up that walks the BINTABLE heap region.

```sql
-- Pull rows from a named extension
SELECT TARGETID, RA, DEC, Z
FROM open_fits_table('/data/redrock-sv1-bright-12345.fits', 'REDSHIFTS')
WHERE ZWARN = 0;

-- Pull by HDU index (rarer; only when EXTNAME isn't set)
SELECT * FROM open_fits_table('/data/catalog.fits', 1);

-- Bound parameter (substituted to a literal at plan time)
SELECT *
FROM open_fits_table($archive, 'CATALOG');
```

**Ingest path.** Dropping a `.fits` file into the dataset pipeline lands it through `FitsFileFormat`, which emits the same shape `open_fits_hdus` does — the most informative default for an unknown file. Recipes that want pixel previews or catalog rows directly should use the corresponding TVF inside an SQL recipe.

### open_h5_meta

`open_h5_meta(path)` -> Rows | QU: 1

Opens an HDF5 file and yields **one row per group and dataset** in the file's tree, including the root. The interrogation TVF for HDF5: read this first to see what's inside an unknown file before pulling rows out with `open_h5_dataset`.

HDF5 files are hierarchical — datasets live at paths inside groups, like a tiny in-file filesystem. ML pipelines, Python tooling, and scientific software all use this structure heavily (e.g. `/embeddings/train/x`, `/metadata/instrument/filter`). The walker visits every node depth-first, so the row stream mirrors the file's logical layout.

Output columns:

| Column | Type | Notes |
|---|---|---|
| `path` | `STRING` | Full in-file path, slash-separated. Root is `/`. |
| `kind` | `STRING` | `'group'` or `'dataset'` |
| `element_kind` | `STRING?` | Dataset element type name (e.g. `'Float32'`, `'Int64'`, `'String'`). NULL for groups. `'Unknown'` for datasets whose dtype isn't supported in v1. |
| `dimensions` | `INT64[]?` | Dataset shape. `[N]` for 1-D, `[R, C]` for 2-D, etc. Empty array for scalar datasets. NULL for groups. |
| `is_scalar` | `BOOLEAN` | TRUE for scalar (rank-0) datasets, FALSE otherwise. FALSE for groups. |
| `is_supported` | `BOOLEAN` | TRUE when `open_h5_dataset` can read this dataset in v1. FALSE for compound, opaque, reference, bit-field, and enumerated dtypes. |
| `attribute_count` | `INT32` | Number of attributes attached to this object. |
| `attributes` | `JSON` | Array of `{name, kind, value}` objects, materialised so callers can filter / extract without going back to the file. |

**Supported element kinds (v1):** signed and unsigned 8/16/32/64-bit integers, IEEE single / double floats, fixed-width and variable-length strings, and booleans. Compound dtypes (HDF5 "struct" cells) and the other rare classes show up with `element_kind = 'Unknown'` and `is_supported = false` so recipes can skip them cleanly.

**Attributes column.** Every HDF5 object can carry metadata attributes — units, descriptions, instrument state, processing history. The TVF reads them eagerly into managed primitives, then renders as a JSON array so SQL can query them with the same JSON operators used for any other JSON column.

```sql
-- Survey a file: what does it contain?
SELECT path, kind, element_kind, dimensions, attribute_count
FROM open_h5_meta('/data/embeddings.h5');

-- Find every supported dataset whose name ends in 'flux'
SELECT path, dimensions
FROM open_h5_meta('/data/spectra.h5')
WHERE kind = 'dataset'
  AND is_supported
  AND path LIKE '%/flux';

-- Pull a specific attribute value from every dataset
SELECT
    path,
    (SELECT attr->>'value' FROM jsonb_array_elements(attributes) AS attr
     WHERE attr->>'name' = 'units') AS units
FROM open_h5_meta('/data/spectra.h5')
WHERE kind = 'dataset';
```

### open_h5_dataset

`open_h5_dataset(path, dataset_path)` -> Rows | QU: 1

Opens a single HDF5 dataset by its in-file path and yields its rows with one typed column. The output schema is the dataset's real schema — the validator peeks the file at plan time to discover the element kind and shape, so projections type-check against actual data:

```sql
SELECT labels FROM open_h5_dataset('/data/cifar.h5', '/labels') WHERE labels > 0;
```

The `labels` column above is typed as `INT32` (or whatever the on-disk dtype really is) at plan time, not at first row.

**Output shape.**

| Source dataset | Yields |
|---|---|
| Scalar (rank-0) | One row, one cell with the scalar value |
| 1-D, length N | N rows, one element per row |
| 2-D, shape (R, C) | R rows, each carrying the C-element row slice as a typed 1-D array |
| 3-D, shape (D₀, D₁, D₂) | D₀ rows, each carrying a multi-dim cell of shape `[D₁, D₂]` |
| 4-D and higher, shape (D₀, D₁, …, Dₙ) | D₀ rows, each carrying a multi-dim cell of shape `[D₁, …, Dₙ]` |

The schema's column carries `FixedShape = [D₁, …, Dₙ]` for rank ≥ 3 datasets, so `cell[i, j, k]` indexing type-checks at plan time. Multi-dim cells are supported across every element kind `open_h5_meta` reports as `is_supported = true` — numerics (signed/unsigned 8-64 bit integers, Float32, Float64, Boolean) and String. The canonical ML shapes work directly:

- MNIST `(60000, 28, 28)` UInt8 → 60000 rows, each a multi-dim UInt8 cell with shape `[28, 28]`
- CIFAR-10 `(50000, 32, 32, 3)` UInt8 → 50000 rows, each shape `[32, 32, 3]`
- Sentence embeddings `(50000, 768)` Float32 → 50000 rows, each a flat 768-element Float32 array (2-D case)

The output column is named after the dataset's leaf segment: `/labels` → column `labels`, `/spectra/flux` → column `flux`. For rank ≥ 3 datasets the column is multi-dim — the `FixedShape` carries the per-row tensor shape so SQL projections can index into it with type-checked bracket access.

**Plan-time schema peek.** Both arguments must be constants at plan time (literals in source, or `$parameter` references that the parameter binder has substituted). The validator opens the file, looks up the dataset, and reads its dtype + dimensions to produce a real `Schema`. Calling with a non-constant argument throws — recipe writers must inline the paths or pass bound parameters.

**Supported element kinds (v1):** same set as `open_h5_meta`'s `is_supported` filter — booleans, signed / unsigned integers (8/16/32/64-bit), Float32, Float64, and strings (both fixed-width and variable-length). Compound dtypes throw `FunctionArgumentException` at validation; recipes can use `open_h5_meta` to detect them ahead of time.

```sql
-- 1-D labels column from a CIFAR-shaped file
SELECT labels FROM open_h5_dataset('/data/cifar.h5', '/labels');

-- 2-D embeddings: one row per training sample, embedding as a Float32 array
SELECT idx.value AS sample_id, e.embeddings
FROM range(0, 1000) AS idx
JOIN open_h5_dataset('/data/embeddings.h5', '/train/x') AS e
  ON idx.value < (SELECT COUNT(*) FROM open_h5_dataset('/data/embeddings.h5', '/train/x'));

-- Bound parameter (substituted to a literal at plan time)
SELECT * FROM open_h5_dataset($archive, '/labels');
```

**Performance.** v1 reads the entire dataset into memory at the start of execution, then iterates rows from the in-memory buffer. Fine for the typical ML dataset (tens to hundreds of MB); a chunked-streaming follow-up will land for files bigger than RAM. The downstream operator pipeline batches the row stream into the planner's standard batch size regardless of how we feed it.

**Ingest path.** Dropping a `.h5` / `.hdf5` / `.hdf` file into the dataset pipeline lands it through `Hdf5FileFormat`, which emits the same shape `open_h5_meta` does. Magic-byte detection picks up extensionless or mislabelled HDF5 files too. Recipes that want specific dataset rows directly should use `open_h5_dataset` inside an SQL recipe.

### open_h5_group

`open_h5_group(path, group_path)` -> Rows

Opens an HDF5 group and yields a **single row** with one column per direct-child dataset, each cell carrying the full dataset shape. The pivot-mode companion to `open_h5_dataset`: when a group represents a logical record made of related-but-different-shaped datasets that you want side-by-side — bitmasks plus their descriptions, parallel label arrays, scalar metadata plus a 1-D array — this lifts them all into one row.

**Shape preservation per cell.**

| Child dataset rank | Yields as a cell |
|---|---|
| Scalar (rank-0) | Scalar of the dataset's element kind |
| 1-D, length N | Flat 1-D array (length N) |
| 2-D, shape (R, C) | Multi-dim cell with `FixedShape = [R, C]` |
| N-D, shape (D₀, …, Dₙ₋₁) | Multi-dim cell with `FixedShape = [D₀, …, Dₙ₋₁]` |

**Direct children only.** Sub-groups are silently skipped — recursive descent isn't in scope here because the row width would explode on deep trees. The natural cross-group access pattern is composition: `open_h5_meta` for discovery, then multiple `open_h5_group` / `open_h5_dataset` calls per record. Datasets whose dtype isn't supported in v1 (compound, opaque, reference, bit field, enumerated, ragged variable-length) are also silently skipped — use `open_h5_meta` and filter by `is_supported = false` if you want to know they're there.

**Plan-time schema peek.** Both arguments must be constants at plan time (literals in source, or `$parameter` references that the parameter binder has substituted). The validator opens the file, walks the group's direct children, and produces a real `Schema` of typed, named columns. Non-constant arguments throw.

**Group-as-record use cases.**

LIGO gravitational-wave data: `/quality/simple` holds nine-element string arrays describing each data-quality bit, plus a 4096-element UInt32 bitmask, plus a scalar metadata string. The four are different shapes but logically one record — `open_h5_group` puts them in one row so SQL can decode bits against their descriptions in a single query.

10x Genomics single-cell `/matrix/features`: three same-length 1-D arrays (`id`, `name`, `feature_type`) that act as parallel columns of a feature table. `open_h5_group` returns one row whose three array columns are the three datasets; downstream `UNNEST` joins them per index if you want per-feature rows.

ML model checkpoint layers: a Keras `/conv1/kernel` group might pair a 4-D weight tensor with a 1-D bias array and a few scalar attribute datasets. `open_h5_group` packages the whole layer as one record.

**Empty group → zero rows.** A group containing only sub-groups, or no children at all, yields nothing (rather than an empty row).

```sql
-- LIGO data-quality bit dictionary: descriptions, short names, bitmask, metadata
SELECT DQDescriptions, DQShortnames, DQmask, GWOSCmeta
FROM open_h5_group('strain.hdf5', '/quality/simple');

-- 10x parallel arrays — yields one row of three arrays, then unnest by index
WITH features AS (
    SELECT id, name, feature_type
    FROM open_h5_group('pbmc.h5', '/matrix/features')
)
SELECT
    id[i + 1] AS gene_id,
    name[i + 1] AS gene_name,
    feature_type[i + 1] AS kind
FROM features, range(0, 1000) AS i;

-- Layer-as-record from a Keras checkpoint
SELECT kernel, bias
FROM open_h5_group('model.h5', '/dense_1');
```

**Bound parameter:**

```sql
SELECT * FROM open_h5_group($archive, $group_path);
```

## See Also

- [Aggregate Functions](aggregate.md) -- grouping and reduction functions
- [Window Functions](window.md) -- per-row computations over partitions
- [SQL Reference](../sql/select.md) -- full SQL dialect documentation
