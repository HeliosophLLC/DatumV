# Data Providers

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md)

DatumIngest reads from seven file-based data providers. Each implements `ITableProvider` and is selected via the `--source` flag or a catalog file.

## CSV

Reads RFC 4180 CSV files. Auto-detects numeric vs string columns from the first 100 rows.

| Option | Description | Default |
|--------|-------------|---------|
| `delimiter` | Field delimiter character | `,` |
| `header` | Whether first row is header | `true` |

Columns: derived from header row. Numeric values parsed as Scalar, others as String.

Implements `IChunkMeasuringProvider` for source index byte-range measurement. The pre-scan is quote-aware, correctly handling multi-line quoted fields and CRLF line endings.

## JSON

Reads JSON files using System.Text.Json streaming. Supports root arrays.

Each object in the array becomes a row with properties as columns. Nested objects/arrays become JsonValue columns for extraction via `json_value()` / `json_query()`.

## JSONL

Reads newline-delimited JSON (JSONL/NDJSON) files where each line is a self-contained JSON object. Streams line-by-line for constant memory usage regardless of file size.

Schema inference samples up to 100 lines and widens column types across the sample. Type inference and value conversion are shared with the JSON provider via `JsonTypeInference`.

Implements `IChunkMeasuringProvider` for source index byte-range measurement. The pre-scan detects data rows by looking for `{` at the start of each line, skipping blank lines and non-object content.

```bash
datum-ingest explore "SELECT * FROM data" --source "jsonl:data=./records.jsonl"
```

## ZIP

Reads ZIP archives via System.IO.Compression.

Yields rows with two columns:
- `file_name` (String) — eager, always available
- `file_bytes` (UInt8Array) — lazy via `LazyDataValue`, decompressed only on access

## HDF5

Reads HDF5 files via PureHDF (managed .NET).

Each 1-D dataset becomes a column. 2-D datasets yield one vector per row. Grouped datasets use flattened names (e.g., `group/dataset`).

## Parquet

Reads Parquet files via Parquet.Net low-level API.

Maps Parquet types to DataKind: INT32/INT64 → Scalar, FLOAT/DOUBLE → Scalar, BYTE_ARRAY (UTF8) → String, BYTE_ARRAY → UInt8Array.

Supports statistics-based row group pruning: when a WHERE predicate is pushed down, the provider reads each row group's min/max column statistics from the Parquet footer metadata and skips row groups that cannot contain matching rows. This avoids reading column data for pruned groups entirely. Use EXPLAIN to see which filter is applied (`statistics filter:` annotation on the scan node) and EXPLAIN ANALYZE to see how many row groups were pruned.

## IDX

Reads IDX binary files — the format used by MNIST, Fashion-MNIST, and similar datasets. Supports all IDX data type codes (uint8, int8, int16, int32, float32, float64) and any dimensionality.

Every table has two columns:
- `index` (Scalar) — 0-based row number, useful for joining separate image and label files
- A data column whose name and type depend on the data:

| Data type | Per-item dims | Column name | DataKind |
|-----------|--------------|-------------|----------|
| uint8 | 0 (scalar) | `value` | UInt8 |
| uint8 | 1 (array) | `data` | UInt8Array |
| uint8 | 2+ (image) | `image` | Image |
| non-uint8 | 0 (scalar) | `value` | Scalar |
| non-uint8 | 1 (vector) | `data` | Vector |
| non-uint8 | 2 (matrix) | `data` | Matrix |
| non-uint8 | 3+ (tensor) | `data` | Tensor |

Uint8 data with 2+ per-item dimensions is materialized as RGBA8888 images via SkiaSharp, enabling direct use of all image functions (resize, crop, grayscale, etc.).

### MNIST example

```bash
datum-ingest query \
  "SELECT resize_image(i.image, 32, 32), l.value \
   FROM images AS i \
   JOIN labels AS l ON i.index = l.index \
   WHERE l.value = 7 \
   INTO 'sevens.parquet'" \
  --source "idx:images=train-images-idx3-ubyte" \
  --source "idx:labels=train-labels-idx1-ubyte"
```

## Source definition format

```
provider:name=path[;key=value;...]
```

Examples:
```
csv:data=./data.csv;delimiter=,;header=true
json:annotations=./coco.json
jsonl:records=./data.jsonl
zip:images=./train2017.zip
hdf5:features=./embeddings.h5
parquet:labels=./labels.parquet
idx:images=./train-images-idx3-ubyte
```

## Catalog file format

JSON array of table descriptors:

```json
[
  {
    "Provider": "csv",
    "Name": "iris",
    "FilePath": "./datasets/iris.csv",
    "Options": { "delimiter": ",", "header": "true" }
  },
  {
    "Provider": "json",
    "Name": "annotations",
    "FilePath": "./datasets/coco.json",
    "Options": {}
  }
]
```

At least one of `--catalog` or `--source` is required. Both can be mixed; `--source` entries override same-named catalog entries.
