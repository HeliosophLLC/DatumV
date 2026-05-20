namespace Heliosoph.DatumV.Web.Execution;

// NDJSON event payloads emitted by the query-stream endpoint. Each event
// is JSON-serialised + a trailing newline; the client parses a line at a
// time and switches on the `type` field.
//
// Cell IDs are monotonic per batch. A "cell" is one statement in a script
// (a single SELECT, one CALL, one DDL command). The lifecycle is:
//   session → (cell_started → schema? → row*|chunk*|truncated → trace_sample* → trace_complete → cell_completed)+ → complete | error
// An error mid-flight terminates the cell + the rest of the batch; the
// final `complete` event still fires for symmetry.

internal sealed record ColumnDescriptor(string Name, string Kind, bool IsArray);

internal sealed record SessionEvent(string Type, string Id);
internal sealed record CellStartedEvent(string Type, string Cell, string Kind, string? Sql);
internal sealed record SchemaEvent(string Type, string Cell, IReadOnlyList<ColumnDescriptor> Columns);
internal sealed record RowEvent(string Type, string Cell, IReadOnlyList<JsonCell> Cells);
internal sealed record TruncatedEvent(string Type, string Cell, int RowCount);
internal sealed record CellCompletedEvent(string Type, string Cell, double ElapsedMs);
internal sealed record CompleteEvent(string Type, double ElapsedMs);
internal sealed record ErrorEvent(string Type, string? Cell, string Message, string? Detail);

// Emitted in place of an error event when the parse-time pre-flight pass
// blocks execution — typically because a query references catalog model
// identifiers that aren't installed yet (the user is offered a
// download flow), or contains likely typos against the function /
// identifier surface. Carries a structured payload so the UI can
// render the install modal + typo hints without re-parsing the
// error message.
internal sealed record PreFlightRequiredEvent(
    string Type,
    string Message,
    IReadOnlyList<PreFlightModelRequirementWire> Models,
    IReadOnlyList<PreFlightDatasetRequirementWire> Datasets,
    IReadOnlyList<PreFlightSuggestionWire> Suggestions);

internal sealed record PreFlightDatasetRequirementWire(
    string TypedReference,
    string Identifier,
    string VariantId,
    string EntryName,
    string DisplayName,
    string Version,
    long ApproxArchiveBytes,
    IReadOnlyList<PreFlightLicenseWire> Licenses);

internal sealed record PreFlightModelRequirementWire(
    string TypedReference,
    string Identifier,
    string CatalogEntryId,
    string? Version,
    bool VersionPinned,
    string Reason,
    int? ApproxSizeMb,
    IReadOnlyList<string> SiblingIdentifiers,
    bool EntryDeprecated,
    string? SupersededBy,
    bool VersionDeprecated,
    string? VersionDeprecationReason,
    IReadOnlyList<PreFlightLicenseWire> Licenses);

// One license a catalog entry requires the user to accept before
// install. <c>Accepted</c> is the server-known acceptance state at
// the moment of pre-flight emission — the client uses it to decide
// whether to render the row as already-accepted vs needing a click,
// without having to issue a separate `/licenses/accepted` request.
// <c>RequiresAcceptance</c> mirrors the catalog's per-license flag; a
// row with `requiresAcceptance=false` is informational only (no
// gate, no Accept button — just a badge).
internal sealed record PreFlightLicenseWire(
    string Id,
    string Title,
    string Summary,
    bool RequiresAcceptance,
    bool Accepted);

internal sealed record PreFlightSuggestionWire(string TypedName, string Suggestion);

// One 1Hz memory-residency sample for the running cell. `rowBytes` is the
// budgeted in-RAM total (operator state + variable-scope payloads + DML
// buffers); `arenaBytes` is informational (mmap, OS-paged); `budgetBytes`
// is null when the query has no configured spill budget.
internal sealed record MemorySampleEvent(
    string Type,
    string Cell,
    double ElapsedMs,
    long RowBytes,
    long ArenaBytes,
    long PeakRowBytes,
    long? BudgetBytes,
    long? VramUsedBytes,
    long? VramTotalBytes);

// One incremental batch of trace entries for the running cell. Emitted
// at 1Hz by the sidecar timer plus once on cell completion (so the tail
// of the trace doesn't get stuck in the ring when no further ticks
// fire). `entries` carry only the spans whose sequence is newer than
// what the client has already seen for this cell. `dropped` reports how
// many entries aged out of the ring between consecutive drains —
// non-zero means the client should surface a "trace overflow" badge.
internal sealed record TraceSampleEvent(
    string Type,
    string Cell,
    IReadOnlyList<TraceEntryWire> Entries,
    int Dropped);

// One completed-span entry on the wire. `tsMs` is offset from the
// cell-start moment (matches memory_sample's `elapsedMs` convention).
// `source` is the short tag ("op" / "fn") so the client can render
// the chip filter directly without re-mapping.
internal sealed record TraceEntryWire(
    long Sequence,
    double TsMs,
    string Source,
    string Name,
    string? Parent,
    double DurationMs);

// Marks the end of the trace stream for a cell. Fires after the cell's
// final trace_sample (which may carry residual entries the sidecar
// didn't flush). `totalEntries` and `totalDropped` are running totals
// the client can use to populate the popover's footer.
internal sealed record TraceCompleteEvent(
    string Type,
    string Cell,
    int TotalEntries,
    int TotalDropped);

/// <summary>
/// Trace-scope toggle passed into <see cref="QueryStreamService"/>. Empty
/// (both flags false) disables tracing entirely — no
/// <see cref="Heliosoph.DatumV.Diagnostics.RecentActivityLog"/> is attached,
/// the engine takes its zero-listener fast path, and no trace events
/// emit. Operators-only is the recommended default for the UI's chip;
/// scalars is opt-in because per-row scalar dispatches generate orders
/// of magnitude more spans.
/// </summary>
public readonly record struct TraceOptions(bool Operators, bool Scalars)
{
    /// <summary>Tracing entirely disabled — no listener attached.</summary>
    public static TraceOptions Off => default;

    /// <summary>True when at least one source is enabled.</summary>
    public bool IsEnabled => Operators || Scalars;
}
