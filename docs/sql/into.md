---
title: INTO
---

## Why Use This

Once you've transformed your data, you need to save it somewhere. INTO writes query results directly to a file — CSV for spreadsheets, Parquet for efficient columnar storage, or HDF5 for ML frameworks. Sharding splits large outputs into manageable chunks, and checkpointing lets you resume after a crash.

Write query results to a file. The output format is inferred from the file extension (`.csv`, `.parquet`, `.h5`/`.hdf5`):

```sql
SELECT * FROM data INTO 'output.csv'
SELECT * FROM data INTO 'output.parquet'
SELECT * FROM data INTO 'output.h5'
```

### Sharding

```sql
-- New shard every 10,000 rows: output_shard_00000.csv, output_shard_00001.csv, ...
SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000

-- New shard every 100MB
SELECT * FROM data INTO 'output.parquet' SHARD ON byte_size 104857600
```

### Checkpointing (resumable writes)

Add `--checkpoint` to resume a sharded write from the last completed shard after a crash or interruption:

```bash
# First run — crashes after writing shards 0–4
datum-ingest query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
  --source csv:data=large.csv --checkpoint

# Re-run the same command — resumes from shard 5
datum-ingest query "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 10000" \
  --source csv:data=large.csv --checkpoint
```

How it works:

1. After each shard is finalized, a `{shard_path}.checkpoint` marker file is written containing the row count, byte count, and source fingerprints.
2. On restart with `--checkpoint`, existing markers are scanned and validated against current source files (size + modification time).
3. If sources match, the pipeline fast-forwards by skipping already-written rows, then continues writing from the next shard index.
4. If the process crashed mid-shard, the incomplete file (which has no marker) is automatically deleted before writing the replacement.
5. After successful completion, all `.checkpoint` marker files are cleaned up.

**Requirements:**

- `SHARD ON` must be specified. Without it, `--checkpoint` prints a warning and is ignored.
- The query must produce rows in a **deterministic, stable order** between runs. Queries with `ORDER BY RANDOM()` or non-deterministic source ordering will produce incorrect results on resume. Ensuring deterministic ordering is the user's responsibility.
- Source data files must not change between runs. If file size or modification time differs, the resume is aborted with an error.

## See Also

- [SELECT](select.md)
- [ORDER BY / LIMIT / OFFSET](order-by.md)
- [PIVOT / UNPIVOT](pivot-unpivot.md)
