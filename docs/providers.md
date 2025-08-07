# Data Providers

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest reads from seven file-based data providers. Each implements `ITableProvider` and is selected via the `--source` flag or a catalog file.

## CSV

Reads RFC 4180 CSV files. Auto-detects numeric vs string columns from the first 100 rows.

| Option | Description | Default |
|--------|-------------|---------|
| `delimiter` | Field delimiter character | `,` |
| `header` | Header row handling | `auto` |

### Header detection

By default (`header=auto`), the provider infers whether the first row is a header by comparing its type profile against subsequent rows. If any column is predominantly numeric in rows 2–20 but the corresponding row-1 value is non-numeric, the first row is treated as a header. Otherwise it is treated as data and columns receive generated names (`col_0`, `col_1`, …).

Set `header=true` to force the first row to be treated as column names, or `header=false` to force generated column names and treat every row as data.

Columns: derived from header row (or generated when headerless). Numeric values parsed as Scalar, others as String.

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

Implements `ISeekableTableProvider` for random-access row reads using PureHDF's `HyperslabSelection` API. Instead of materialising entire datasets, the provider constructs a hyperslab selection covering only the requested row range and passes it to `dataset.Read<T[]>(fileSelection: selection)`. For multi-dimensional datasets (vectors), an N-D selection slices the first dimension (rows) while spanning all remaining dimensions. This enables chunk-level seeking during index pruning and sorted index scan for ORDER BY optimisation.

## Parquet

Reads Parquet files via Parquet.Net low-level API.

Maps Parquet types to DataKind: INT32/INT64 → Scalar, FLOAT/DOUBLE → Scalar, BYTE_ARRAY (UTF8) → String, BYTE_ARRAY → UInt8Array.

Supports statistics-based row group pruning: when a WHERE predicate is pushed down, the provider reads each row group's min/max column statistics from the Parquet footer metadata and skips row groups that cannot contain matching rows. This avoids reading column data for pruned groups entirely. Use EXPLAIN to see which filter is applied (`statistics filter:` annotation on the scan node) and EXPLAIN ANALYZE to see how many row groups were pruned.

Implements `ISeekableTableProvider` for random-access row reads. The provider builds a cumulative row offset table from row group metadata and binary-searches for the row group(s) spanning the requested range. Only the touched row groups are materialised; leading and trailing groups are skipped entirely. This enables chunk-level seeking during index pruning, sorted index scan for ORDER BY optimisation, and exact row seek for WHERE predicates.

## IDX

Reads IDX binary files — the format used by MNIST, Fashion-MNIST, and similar datasets. Supports all IDX data type codes (uint8, int8, int16, int32, float32, float64) and any dimensionality.

Implements `ISeekableTableProvider` for random-access row reads, enabling chunk-level seeking during index pruning and sorted index scan for ORDER BY optimization.

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
  --source "images=train-images-idx3-ubyte" \
  --source "labels=train-labels-idx1-ubyte"
```

## Source definition format

Sources can be specified with or without an explicit provider prefix:

```
name=path[;key=value;...]            # auto-detect provider from file
provider:name=path[;key=value;...]   # explicit provider
```

Examples:
```
data=./data.csv
images=./train-images-idx3-ubyte
data=./data.csv;delimiter=,;header=true
csv:data=./data.csv;delimiter=,;header=true
json:annotations=./coco.json
jsonl:records=./data.jsonl
zip:images=./train2017.zip
hdf5:features=./embeddings.h5
parquet:labels=./labels.parquet
idx:images=./train-images-idx3-ubyte
```

When the provider prefix is omitted, the format is detected automatically (see below). The explicit `provider:name=path` form is always available as an override.

### Directory sources

When `--source` is given a directory path instead of a file, all supported files in that directory are auto-discovered and registered as tables. Table names are derived from filenames using `FileFormatDetector.DeriveTableName` — container extensions like `.datum` are stripped, so `order_products__prior.csv.datum` becomes table `order_products__prior.csv`. Sidecar files (`.datum-index`, `.datum-manifest`, `.datum-schema`) are matched automatically.

This mirrors the behaviour of the gRPC compute backend's `DatasetCatalogFactory`, which performs the same directory scan when a dataset is loaded by ID.

```bash
# Load every supported file from a directory
datum-ingest shell --source ./datasets/instacart

# Query against auto-discovered tables
datum-ingest explore "SELECT * FROM [orders.csv] LIMIT 10" --source ./datasets/instacart
```

## Format auto-detection

When a `--source` definition omits the provider prefix, or when the programmatic API uses `catalog.Register(name, filePath)`, the file format is detected using a three-tier strategy:

### 1. File extension

| Extension | Provider |
|-----------|----------|
| `.csv`, `.tsv` | csv |
| `.json` | json |
| `.jsonl`, `.ndjson` | jsonl |
| `.parquet`, `.pq` | parquet |
| `.hdf5`, `.h5`, `.hdf` | hdf5 |
| `.zip` | zip |
| `.idx` | idx |

Extension matching is case-insensitive.

### 2. Filename pattern

Files matching the MNIST-style IDX naming convention (e.g. `train-images-idx3-ubyte`, `t10k-labels-idx1-ubyte`) are detected as IDX regardless of extension.

### 3. Magic bytes

If the extension and filename pattern are inconclusive, the first 8 bytes of the file are read to check for known signatures:

| Signature | Provider |
|-----------|----------|
| `PAR1` | parquet |
| `\x89HDF\r\n\x1a\n` | hdf5 |
| `PK\x03\x04` | zip |
| `\x00\x00` + valid IDX type code + dimension count ≥ 1 | idx |
| `{` or `[` (first non-whitespace byte) | json |

If all three tiers fail, an error is raised suggesting the explicit `provider:name=path` format.

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

At least one of `--catalog` or `--source` is required. Both can be mixed; `--source` entries override same-named catalog entries. Directory sources can be combined with individual file sources and catalog files.
