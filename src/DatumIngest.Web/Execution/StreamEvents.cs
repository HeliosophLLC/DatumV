namespace DatumIngest.Web.Execution;

// NDJSON event payloads emitted by the query-stream endpoint. Each event
// is JSON-serialised + a trailing newline; the client parses a line at a
// time and switches on the `type` field. Names + shapes mirror DevWeb so
// the client-side NDJSON parser is identical.
//
// Cell IDs are monotonic per batch. A "cell" is one statement in a script
// (a single SELECT, one CALL, one DDL command). The lifecycle is:
//   session → (cell_started → schema? → row*|chunk*|truncated → cell_completed)+ → (trace?) → complete | error
// An error mid-flight terminates the cell + the rest of the batch; the
// final `complete` event still fires for symmetry.

internal sealed record ColumnDescriptor(string Name, string Kind, bool IsArray);

internal sealed record SessionEvent(string Type, string Id);
internal sealed record CellStartedEvent(string Type, string Cell, string Kind, string? Sql);
internal sealed record ChunkWireEvent(string Type, string Cell, string Model, string Text);
internal sealed record SchemaEvent(string Type, string Cell, IReadOnlyList<ColumnDescriptor> Columns);
internal sealed record RowEvent(string Type, string Cell, IReadOnlyList<JsonCell> Cells);
internal sealed record TruncatedEvent(string Type, string Cell, int RowCount);
internal sealed record TraceEvent(string Type, string Cell, string Text);
internal sealed record CellCompletedEvent(string Type, string Cell, double ElapsedMs);
internal sealed record CompleteEvent(string Type, double ElapsedMs);
internal sealed record ErrorEvent(string Type, string? Cell, string Message, string? Detail);
