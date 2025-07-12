# Data Providers

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Statistics & Manifest](statistics.md) · [Architecture](architecture.md) · [Programmatic API](api.md)

DatumIngest reads from six file-based data providers. Each implements `ITableProvider` and is selected via the `--source` flag or a catalog file.

## CSV

Reads RFC 4180 CSV files. Auto-detects numeric vs string columns from the first 100 rows.

| Option | Description | Default |
|--------|-------------|---------|
| `delimiter` | Field delimiter character | `,` |
| `header` | Whether first row is header | `true` |

Columns: derived from header row. Numeric values parsed as Scalar, others as String.

## JSON

Reads JSON files using System.Text.Json streaming. Supports root arrays.

Each object in the array becomes a row with properties as columns. Nested objects/arrays become JsonValue columns for extraction via `json_value()` / `json_query()`.

## JSONL

Reads newline-delimited JSON (JSONL/NDJSON) files where each line is a self-contained JSON object. Streams line-by-line for constant memory usage regardless of file size.

Schema inference samples up to 100 lines and widens column types across the sample. Type inference and value conversion are shared with the JSON provider via `JsonTypeInference`.

```bash
dq explore "SELECT * FROM data" --source "jsonl:data=./records.jsonl"
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
