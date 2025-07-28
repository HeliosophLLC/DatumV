# Compute Backend (gRPC)

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Language Server](language-server.md) · [Programmatic API](api.md)

DatumIngest.Compute is a gRPC service library that exposes the DatumIngest query engine over the network. It wraps the same `SessionManager` and `CommandDispatcher` used by the interactive shell, enabling remote session management, SQL query streaming, and administrative operations. Embed it in any ASP.NET application with two method calls.

## Embedding in an ASP.NET Host

### Minimal Setup

```csharp
using DatumIngest.Compute;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDatumCompute(options =>
{
    options.ApiKey = "my-secret-key";
});

WebApplication app = builder.Build();

app.MapDatumCompute();
app.Run();
```

### With a Custom Dataset Store

The compute backend optionally depends on `IDatasetStore` (defined in `DatumIngest.Server`) for pulling remote datasets into sessions. Register your implementation **before** calling `AddDatumCompute`:

```csharp
using DatumIngest.Compute;
using DatumIngest.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register your own dataset store (e.g., blob storage, S3).
builder.Services.AddSingleton<IDatasetStore>(new MyBlobDatasetStore("connection-string"));

builder.Services.AddDatumCompute(options =>
{
    options.ApiKey = builder.Configuration["ApiKey"]!;
    options.MaxReceiveMessageSize = 128 * 1024 * 1024; // 128 MB
});

WebApplication app = builder.Build();

app.MapDatumCompute();
app.Run();
```

If no `IDatasetStore` is registered, the `SessionManager` runs in local-only mode — sessions use empty catalogs and sources are added at runtime via `AddSource`.

### IDatasetStore Contract

Implement `DatumIngest.Server.IDatasetStore` to integrate with your storage backend:

```csharp
public interface IDatasetStore
{
    /// <summary>Checks whether the dataset is already available locally.</summary>
    Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken);

    /// <summary>Pulls the dataset to local disk, returning the local directory path.</summary>
    Task<string> PullAsync(string datasetId, CancellationToken cancellationToken);

    /// <summary>Removes the dataset from local storage.</summary>
    Task EvictAsync(string datasetId, CancellationToken cancellationToken);
}
```

A built-in `LocalFileDatasetStore` is provided for filesystem-backed deployments:

```csharp
builder.Services.AddSingleton<IDatasetStore>(
    new LocalFileDatasetStore("/data/datasets"));
```

### Overriding Defaults

`AddDatumCompute` uses `TryAddSingleton` for the engine services, so you can register your own before calling it:

```csharp
// Custom function registry with extra functions.
builder.Services.AddSingleton(myCustomFunctionRegistry);

// Custom session manager with specific configuration.
builder.Services.AddSingleton(mySessionManager);

// AddDatumCompute will skip these — your registrations take precedence.
builder.Services.AddDatumCompute(options => options.ApiKey = "key");
```

### DatumComputeOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ApiKey` | `string` | `""` | API key clients must send in `x-api-key` header. Empty disables auth. |
| `MaxReceiveMessageSize` | `int` | `67108864` (64 MB) | Maximum inbound gRPC message size in bytes. |
| `MaxSendMessageSize` | `int` | `67108864` (64 MB) | Maximum outbound gRPC message size in bytes. |
| `QueryTimeoutSeconds` | `int?` | `null` | Server-wide default query deadline in seconds. `null` = no deadline. |
| `MaxOutputRows` | `long?` | `null` | Server-wide default maximum rows per query. `null` = no limit. |
| `ThrottleDelayMilliseconds` | `int?` | `null` | Server-wide default throttle delay in ms. `null` = no throttle. |
| `MaxQueryUnits` | `long?` | `null` | Server-wide default QU budget per query. `null` = no limit. |
| `MemoryBudgetBytes` | `long?` | `null` | Server-wide default memory budget for spill-to-disk joins in bytes. `null` = keep everything in memory. |

## Calling with grpcurl

```bash
# Create a session
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"role": "admin"}' \
  localhost:5050 datum_compute.DatumCompute/CreateSession

# Add a data source
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"session_id": "<SESSION_ID>", "source_definition": "csv:wine=winequality-red.csv"}' \
  localhost:5050 datum_compute.DatumCompute/AddSource

# Run a query (server-streaming)
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"session_id": "<SESSION_ID>", "sql": "SELECT alcohol, quality FROM wine LIMIT 5"}' \
  localhost:5050 datum_compute.DatumCompute/Query

# Run a query with parameters
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"session_id": "<SESSION_ID>", "sql": "SELECT alcohol, quality FROM wine WHERE quality > $min_quality", "parameters": {"min_quality": {"scalar_value": 6}}}' \
  localhost:5050 datum_compute.DatumCompute/Query

# List tables
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"session_id": "<SESSION_ID>"}' \
  localhost:5050 datum_compute.DatumCompute/ListTables

# Destroy session
grpcurl -plaintext \
  -H "x-api-key: my-secret-key" \
  -d '{"session_id": "<SESSION_ID>"}' \
  localhost:5050 datum_compute.DatumCompute/DestroySession
```

## Authentication

All RPCs require an `x-api-key` metadata header when `DatumComputeOptions.ApiKey` is set to a non-empty value. Requests without a valid key receive `StatusCode.Unauthenticated`. Set `ApiKey` to an empty string to disable authentication entirely.

```
x-api-key: <your-api-key>
```

The key is compared using ordinal (case-sensitive) string comparison.

## Service Reference

The service is defined in `datum_compute.proto` under the `datum_compute` package.

### Session Management

#### CreateSession

Creates a new session on the compute backend.

| Field | Type | Description |
|-------|------|-------------|
| `role` | `string` | `"user"` or `"admin"`. Defaults to `user` if empty. |
| `dataset_id` | `string` | Dataset to load via `IDatasetStore`. Empty for local sessions where sources are added at runtime via `AddSource`. |
| `query_timeout_seconds` | `int32` | Per-session query deadline override. `0` = server default, positive = override, negative = disable. |
| `max_output_rows` | `int64` | Per-session row budget override. `0` = server default, positive = override, negative = disable. |
| `throttle_delay_ms` | `int32` | Per-session throttle delay override. `0` = server default, positive = override, negative = disable. |
| `max_query_units` | `int64` | Per-session QU budget override. `0` = server default, positive = override, negative = disable. |
| `memory_budget_bytes` | `int64` | Per-session memory budget for spill-to-disk joins. `0` = server default, positive = override (bytes), negative = disable (all in-memory). |

**Returns:** `session_id` — a GUID identifying the session for all subsequent calls.

When `dataset_id` is provided, the backend pulls the dataset to local storage and auto-discovers all supported files (CSV, JSON, JSONL, Parquet, HDF5, ZIP, IDX) in the resulting directory. Each file becomes a table named after its filename without extension. Sidecar `.datum-index`, `.datum-manifest`, and `.datum-schema` files are also discovered automatically via `catalog.DiscoverSidecars()`, enabling chunk pruning, manifest-driven cardinality estimation, and cached schema resolution for all sessions created from the dataset. If no `IDatasetStore` is registered, passing a non-empty `dataset_id` returns `FailedPrecondition`.

**Roles:**
- **User** — can run queries, inspect schemas, list tables/providers/functions, and view explain plans.
- **Admin** — all user capabilities, plus: add sources (`.source`), list sessions (`.sessions`), kill queries (`.kill`).

#### DestroySession

Destroys a session and frees associated resources.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID to destroy. |

**Errors:** `NotFound` if the session does not exist; `InvalidArgument` if the GUID is malformed.

### Query Execution

#### Query (server-streaming)

Executes a SQL query and streams result rows back. The first `QueryRow` message includes the schema; subsequent messages carry only values.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID. |
| `sql` | `string` | SQL query to execute. |
| `parameters` | `map<string, DataValueMessage>` | Named parameter bindings for `$name` placeholders in the query. Keys are parameter names without the `$` prefix. Values are typed `DataValueMessage` instances. Empty map if the query has no parameters. |

**Stream response (`QueryRow`):**

| Field | Type | Description |
|-------|------|-------------|
| `schema` | `SchemaMessage` | Set on the first row only. Column names, kinds, and nullability. |
| `values` | `repeated DataValueMessage` | One value per column, in schema order. |
| `query_units` | `int64` | Running total of Query Units consumed by function invocations. The value on the last row is the definitive cost for the query. |

**Errors:** `InvalidArgument` on syntax errors or query failures; `NotFound` if the session does not exist.

**Cancellation:** The stream can be stopped in two ways:

1. **Client disconnect** — disposing the streaming call (`stream.Dispose()`) triggers gRPC call cancellation on the server.
2. **Admin kill** — another session calls `KillQuery` with this session as the target. The server links both the gRPC call token and the session token, so either cancellation source stops the row stream immediately.

**Governance enforcement:** The query streaming loop enforces the session's resource governance limits (see [Resource Governance](#resource-governance) below). A deadline triggers `DeadlineExceeded`, a row budget triggers `ResourceExhausted`, and a throttle delay injects periodic pauses.

Returns the execution plan for a SQL query without running it.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID. |
| `sql` | `string` | SQL query to explain. |
| `analyze` | `bool` | Reserved for future use. When `true`, the query will be executed and runtime metrics (row counts, timing) will be collected. Not yet implemented. |

**Response fields:**

| Field | Type | Description |
|-------|------|-------------|
| `plan_text` | `string` | Human-readable rendered plan tree (always populated). |
| `root` | `ExplainPlanNodeMessage` | Structured plan tree root for programmatic inspection. |

**`ExplainPlanNodeMessage`:**

| Field | Type | Description |
|-------|------|-------------|
| `operator_name` | `string` | Operator type (e.g. `Scan`, `Filter`, `INNER Join`, `Sort`). |
| `details` | `string` | Operator-specific configuration (table name, predicate, columns). |
| `children` | `repeated ExplainPlanNodeMessage` | Child operator subtrees. |
| `child_label` | `string` | Edge label from parent (e.g. `probe`, `build` for hash joins). |
| `warnings` | `repeated string` | Performance warnings (e.g. `CROSS JOIN`, `LIKE forces full scan`). |
| `annotations` | `repeated string` | Static plan annotations (e.g. `bounded top-N sort (N=100)`). |
| `runtime` | `ExplainRuntimeMetrics` | Runtime metrics — populated only when `analyze = true`. |
| `estimated_rows` | `int64` | Estimated row count from the cost model (0 when unknown). When a `.datum-manifest` sidecar is available, estimates use manifest row counts and NDV-based selectivity instead of fixed heuristics. |
| `has_estimated_rows` | `bool` | Whether `estimated_rows` was populated (distinguishes 0 from unknown). |

**`ExplainRuntimeMetrics`:**

| Field | Type | Description |
|-------|------|-------------|
| `rows_produced` | `int64` | Rows produced by this operator. |
| `rows_consumed` | `int64` | Rows consumed from child operators. |
| `self_time_us` | `int64` | Self time in microseconds (excludes children). |
| `total_time_us` | `int64` | Total time in microseconds (includes children). |
| `runtime_annotations` | `repeated string` | Runtime-only annotations (e.g. Parquet row group pruning stats). |

### Catalog Inspection

#### GetSchema

Returns the column schema of a registered table.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID. |
| `table_name` | `string` | Name of the table. |

**Returns:** `repeated ColumnInfoMessage` — each with `name`, `kind`, and `nullable`.

#### ListTables / ListProviders

Returns string lists of registered tables or format providers.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID. |

**Returns:** `repeated string items`.

#### ListFunctions

Returns all available functions with parameter metadata.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID. |

**Response (`ListFunctionsResponse`):** `repeated FunctionInfoMessage functions`.

**`FunctionInfoMessage`:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | The SQL function name. |
| `parameters` | `repeated ParameterInfoMessage` | Ordered parameter list. |
| `return_type` | `string` | Return type name (e.g. `"Scalar"`), empty if context-dependent. |
| `is_table_valued` | `bool` | Whether this is a table-valued function (used in `FROM`/`JOIN`). |
| `query_unit_cost` | `int32` | Base query-unit cost per invocation. Image functions may incur additional resolution-dependent cost (see [Functions Reference](functions.md)). |

**`ParameterInfoMessage`:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | Parameter name (e.g. `"value"`, `"start"`). |
| `kind` | `ParameterKindValue` | Expected data kind for this parameter. |
| `required` | `bool` | Whether this parameter is required. |

**`ParameterKindValue` enum:**

| Value | Meaning |
|-------|---------|
| `PARAMETER_KIND_ANY` (0) | Accepts any type (polymorphic parameter). |
| `PARAMETER_KIND_UINT8` (1) | `UInt8` |
| `PARAMETER_KIND_SCALAR` (2) | `Scalar` |
| `PARAMETER_KIND_VECTOR` (3) | `Vector` |
| `PARAMETER_KIND_MATRIX` (4) | `Matrix` |
| `PARAMETER_KIND_TENSOR` (5) | `Tensor` |
| `PARAMETER_KIND_UINT8_ARRAY` (6) | `UInt8Array` |
| `PARAMETER_KIND_IMAGE` (7) | `Image` |
| `PARAMETER_KIND_STRING` (8) | `String` |
| `PARAMETER_KIND_DATE` (9) | `Date` |
| `PARAMETER_KIND_DATE_TIME` (10) | `DateTime` |
| `PARAMETER_KIND_JSON_VALUE` (11) | `JsonValue` |

### Source Management

#### AddSource *(admin only)*

Adds a data source to the session's catalog at runtime.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID (must be admin). |
| `source_definition` | `string` | Source definition string. |

**Source definition format:** `provider:name=path[;key=value;...]`

Examples:
- `csv:sales=data/sales.csv`
- `parquet:events=events.parquet`
- `csv:data=file.csv;delimiter=|;header=true`
- `sales=data/sales.csv` *(auto-detects provider from extension)*

**Errors:** `InvalidArgument` on parse failure or permission denial (user role).

### Administrative Operations

#### ListSessions *(admin only)*

Lists all active sessions on the server.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Calling session GUID (must be admin). |

**Returns:** `repeated SessionInfoMessage` with `session_id`, `role`, `dataset_id`, `created_at`, `last_activity_at`, `query_count`, `total_query_units`.

**Errors:** `PermissionDenied` if the calling session is not admin.

#### KillQuery *(admin only)*

Cancels a running query on another session. The target session's cancellation token is linked into the active query's execution pipeline, so cancellation takes effect immediately — the server stops reading rows and the streaming call ends with `OperationCanceledException`.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Calling session GUID (must be admin). |
| `target_session_id` | `string` | Session whose query should be cancelled. |

**Returns:** `message` — confirmation text (e.g. `"Cancelled active query on session '...'"`). The target session remains open and can run new queries.

**Errors:** `InvalidArgument` if the target session is not found or the GUID is malformed.

#### CancelQuery

Cancels the active query on the caller's own session. Unlike `KillQuery`, this does not require admin privileges — any session can cancel its own query. The session's cancellation token fires, stopping the execution pipeline immediately. The session remains open and can run new queries.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID of the caller. |

**Returns:** `message` — confirmation text (e.g. `"Active query cancelled."`). The session is immediately ready for a new query.

**Errors:** `NotFound` if the session ID is not found.

#### GetUsage

Returns accumulated usage metrics for a session. Unlike `ListSessions`, this does not require admin privileges — any session can query its own usage.

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Session GUID to query. |

**Response:**

| Field | Type | Description |
|-------|------|-------------|
| `session_id` | `string` | Echo of the requested session ID. |
| `total_query_units` | `int64` | Cumulative Query Units consumed across all queries in this session. |
| `query_count` | `int32` | Number of queries executed in this session. |

## Data Types

The `DataValueMessage` uses a `oneof` discriminated union to carry typed values:

| DataKind | Proto field | Wire type | Notes |
|----------|-------------|-----------|-------|
| `UInt8` | `uint8_value` | `uint32` | Single byte (0–255) |
| `Scalar` | `scalar_value` | `float` | 32-bit float |
| `String` | `string_value` | `string` | UTF-8 text |
| `Date` | `date_value` | `string` | ISO 8601 date (`2024-06-15`) |
| `DateTime` | `date_time_value` | `string` | ISO 8601 round-trip (`O` format) |
| `JsonValue` | `json_value` | `string` | Raw JSON string |
| `UInt8Array` | `uint8_array_value` | `bytes` | Binary data |
| `Image` | `image_value` | `bytes` | Encoded image bytes |
| `Vector` | `vector_value` | `VectorMessage` | `repeated float values` |
| `Matrix` | `matrix_value` | `MatrixMessage` | `rows`, `columns`, `repeated float values` |
| `Tensor` | `tensor_value` | `TensorMessage` | `repeated int32 shape`, `repeated float values` |

Null values set `is_null = true` with no `oneof` field populated.

## .NET Client Package

The `DatumIngest.Compute.Client` package provides a generated gRPC client and a convenience `DatumComputeConnection` wrapper. It depends only on `Grpc.Net.Client` — no ASP.NET Core or server-side dependencies required.

```bash
dotnet add package DatumIngest.Compute.Client
```

### Using DatumComputeConnection

`DatumComputeConnection` handles channel creation and optional API key header injection:

```csharp
using DatumIngest.Compute.Client;
using DatumIngest.Compute.Grpc;

using DatumComputeConnection connection = new("http://localhost:5050", apiKey: "my-secret-key");

// Create a session.
CreateSessionResponse session = await connection.Client.CreateSessionAsync(
    new CreateSessionRequest { Role = "admin" });

string sessionId = session.SessionId;

// Add a CSV source.
await connection.Client.AddSourceAsync(
    new AddSourceRequest
    {
        SessionId = sessionId,
        SourceDefinition = "csv:wine=winequality-red.csv",
    });

// Stream query results.
using AsyncServerStreamingCall<QueryRow> stream = connection.Client.Query(
    new QueryRequest
    {
        SessionId = sessionId,
        Sql = "SELECT alcohol, quality FROM wine WHERE quality > $min_quality LIMIT 10",
        Parameters =
        {
            ["min_quality"] = new DataValueMessage { ScalarValue = 6 },
        },
    });

SchemaMessage? schema = null;

await foreach (QueryRow row in stream.ResponseStream.ReadAllAsync())
{
    if (row.Schema is not null)
    {
        schema = row.Schema;
        Console.WriteLine(string.Join("\t",
            schema.Columns.Select(column => column.Name)));
    }

    Console.WriteLine(string.Join("\t",
        row.Values.Select(value => value.IsNull ? "NULL" : FormatValue(value))));
}

// Clean up.
await connection.Client.DestroySessionAsync(
    new DestroySessionRequest { SessionId = sessionId });
```

#### Cancelling a streaming query

Dispose the streaming call to stop the server from sending more rows:

```csharp
using AsyncServerStreamingCall<QueryRow> stream = connection.Client.Query(
    new QueryRequest { SessionId = sessionId, Sql = "SELECT * FROM huge_table" });

await foreach (QueryRow row in stream.ResponseStream.ReadAllAsync())
{
    if (ShouldStop(row))
    {
        break; // Exiting the loop disposes the stream, cancelling the server call.
    }
}
```

To cancel the active query on your own session from a separate call
(e.g. a UI "Cancel" button or a concurrent task):

```csharp
string message = await connection.CancelActiveQueryAsync(sessionId);
```

To cancel another session's query (requires admin):

```csharp
string message = await connection.CancelQueryAsync(
    adminSessionId, targetSessionId);
```

To query accumulated usage for a session:

```csharp
GetUsageResponse usage = await connection.GetUsageAsync(sessionId);
Console.WriteLine($"Queries: {usage.QueryCount}, Total QU: {usage.TotalQueryUnits}");
```

static string FormatValue(DataValueMessage value)
{
    if (value.HasScalarValue) return value.ScalarValue.ToString("F2");
    if (value.HasStringValue) return value.StringValue;
    if (value.HasUint8Value) return value.Uint8Value.ToString();
    return value.ToString();
}
```

### Using the Generated Client Directly

For advanced scenarios (custom channel options, dependency injection, interceptors), use `DatumCompute.DatumComputeClient` directly with your own `GrpcChannel`:

```csharp
using Grpc.Net.Client;
using Grpc.Core;
using DatumIngest.Compute.Grpc;

GrpcChannel channel = GrpcChannel.ForAddress("http://localhost:5050");
DatumCompute.DatumComputeClient client = new(channel);

Metadata headers = new() { { "x-api-key", "my-secret-key" } };
CallOptions callOptions = new(headers: headers);

CreateSessionResponse session = await client.CreateSessionAsync(
    new CreateSessionRequest { Role = "user" }, callOptions);
```

## Python Client Example

```python
import grpc
import datum_compute_pb2
import datum_compute_pb2_grpc

# Connect with API key metadata.
channel = grpc.insecure_channel("localhost:5050")
stub = datum_compute_pb2_grpc.DatumComputeStub(channel)
metadata = [("x-api-key", "my-secret-key")]

# Create a session.
response = stub.CreateSession(
    datum_compute_pb2.CreateSessionRequest(role="admin"),
    metadata=metadata,
)
session_id = response.session_id

# Add a source.
stub.AddSource(
    datum_compute_pb2.AddSourceRequest(
        session_id=session_id,
        source_definition="csv:wine=winequality-red.csv",
    ),
    metadata=metadata,
)

# Stream query results.
for row in stub.Query(
    datum_compute_pb2.QueryRequest(
        session_id=session_id,
        sql="SELECT alcohol, quality FROM wine WHERE quality > $min_quality LIMIT 5",
        parameters={"min_quality": datum_compute_pb2.DataValueMessage(scalar_value=6)},
    ),
    metadata=metadata,
):
    if row.schema.columns:
        print([col.name for col in row.schema.columns])
    print([v.scalar_value if v.HasField("scalar_value") else str(v) for v in row.values])

# Clean up.
stub.DestroySession(
    datum_compute_pb2.DestroySessionRequest(session_id=session_id),
    metadata=metadata,
)
```

Generate the Python stubs from the proto file:

```bash
python -m grpc_tools.protoc \
  -I src/DatumIngest.Compute/Protos \
  --python_out=. \
  --grpc_python_out=. \
  datum_compute.proto
```

## Resource Governance

Sessions can be configured with resource limits to protect multi-tenant deployments. Limits are set server-wide via `DatumComputeOptions` and can be overridden per-session through `CreateSessionRequest` fields.

### Override Semantics

Each governance field in `CreateSessionRequest` follows three-state semantics:

| Request value | Behavior |
|---------------|----------|
| `0` | Use the server default (from `DatumComputeOptions`). |
| Positive | Override with this value. |
| Negative | Explicitly disable (no limit), even if a server default is set. |

### Mechanisms

**Query deadline (`query_timeout_seconds`):** A wall-clock timeout applied to the query streaming loop. When the deadline fires, the server cancels the query and returns `DeadlineExceeded`. The deadline covers both query planning and row streaming.

**Row budget (`max_output_rows`):** The maximum number of rows the server will stream for a single query. When the budget is exceeded, the server stops streaming and returns `ResourceExhausted`. Rows already sent are not retracted — the client receives a partial result set followed by the error status.

**Throttle delay (`throttle_delay_ms`):** An artificial pause (in milliseconds) injected every 100 rows during streaming, yielding CPU time to other sessions. Designed for batch export workloads where deadline and row budget are not appropriate. The throttle does not produce an error — it simply slows the stream.

**Query Unit budget (`max_query_units`):** The maximum total Query Units (QU) a single query may accumulate from scalar function invocations. Each function has a base QU cost reflecting its computational weight (see [Functions Reference — cost tiers](functions.md)). Image analysis and transform functions additionally incur a supplemental cost proportional to input resolution: `floor(pixelCount / 100,000)` QU per invocation. The server checks the running total after each row; when the budget is exceeded it stops streaming and returns `ResourceExhausted`. Clients can monitor per-row cost via the `query_units` field on `QueryRow`, and query cumulative session cost via `GetUsage`.

**Memory budget (`memory_budget_bytes`):** The maximum memory (in bytes) the join operator may use before spilling partitions to temporary files on disk. When set, joins use a Grace hash join strategy: both sides are partitioned by hash key, memory usage is sampled, and the largest in-memory partition spills to disk when usage exceeds 75% of the budget. Spilled partitions are joined in a second pass by reading rows back from disk. When the budget is `null` (or explicitly disabled with a negative override), all join data is held in memory as before. Typical values are `512MB`–`2GB` depending on available server RAM.

### Configuration Example

```csharp
builder.Services.AddDatumCompute(options =>
{
    options.ApiKey = "secret";
    options.QueryTimeoutSeconds = 300;           // 5-minute default deadline.
    options.MaxOutputRows = 100_000;             // 100k row budget by default.
    options.MaxQueryUnits = 1_000_000;            // 1M QU budget by default.
    options.MemoryBudgetBytes = 512 * 1024 * 1024; // 512 MB spill-to-disk budget.
    // ThrottleDelayMilliseconds left null — no throttle by default.
});
```

A batch export client can then override per-session:

```csharp
// Disable deadline and row budget, enable throttle for CPU yielding.
CreateSessionResponse session = await connection.Client.CreateSessionAsync(
    new CreateSessionRequest
    {
        Role = "user",
        QueryTimeoutSeconds = -1,
        MaxOutputRows = -1,
        ThrottleDelayMs = 10,
    });
```

A data-intensive workload can request a memory budget for spill-to-disk joins:

```csharp
// Enable spill-to-disk joins with a 1 GB memory budget.
CreateSessionResponse session = await connection.Client.CreateSessionAsync(
    new CreateSessionRequest
    {
        Role = "user",
        MemoryBudgetBytes = 1024 * 1024 * 1024,
    });
```

## Error Codes

| gRPC Status Code | When |
|------------------|------|
| `Unauthenticated` | Missing or invalid `x-api-key` header |
| `InvalidArgument` | Malformed session ID, bad SQL syntax, invalid source definition |
| `NotFound` | Session ID not found, table not found |
| `PermissionDenied` | User-role session attempting admin operation |
| `ResourceExhausted` | Row budget or Query Unit budget exceeded during query streaming |
| `DeadlineExceeded` | Query deadline fired during execution |
| `Cancelled` | Query cancelled by CancelQuery, KillQuery, or session cancellation |
| `Internal` | Unexpected server error |

## Message Size Limits

Both send and receive limits are set to **64 MB** by default, configurable via `DatumComputeOptions.MaxReceiveMessageSize` and `DatumComputeOptions.MaxSendMessageSize`. This accommodates large result sets with image or tensor columns.
