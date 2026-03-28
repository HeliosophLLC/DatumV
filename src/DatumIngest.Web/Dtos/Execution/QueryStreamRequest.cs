using System.Text.Json;

namespace DatumIngest.Web.Dtos.Execution;

/// <summary>
/// Request payload for <c>POST /api/query/stream</c>. The endpoint returns
/// NDJSON; this DTO carries only what's needed to kick off a batch.
/// </summary>
/// <param name="Sql">The SQL text to execute. Multi-statement batches accepted.</param>
/// <param name="MaxRows">Per-cell row cap; defaults to 1000 when omitted or non-positive.</param>
/// <param name="Trace">
/// Trace scope for this run. <see langword="null"/> or absent disables tracing;
/// <c>{ operators: true }</c> captures operator-pull spans;
/// <c>{ operators: true, scalars: true }</c> additionally captures per-scalar-call
/// dispatches (much higher volume — opt-in).
/// </param>
/// <param name="Parameters">
/// Optional <c>$name</c> bindings. Inline scalars use <see cref="ParameterJson.Value"/>;
/// binary kinds (<c>Image</c> / <c>Audio</c> / <c>Video</c> / <c>UInt8</c> array)
/// use <see cref="ParameterJson.Ref"/> to name a sibling multipart part carrying
/// the bytes — only meaningful when the request is <c>multipart/form-data</c>.
/// </param>
public sealed record QueryStreamRequest(
    string Sql,
    int? MaxRows = null,
    TraceOptionsJson? Trace = null,
    Dictionary<string, ParameterJson>? Parameters = null);

/// <summary>
/// Trace-scope JSON envelope. Mirror of the server-side
/// <see cref="DatumIngest.Web.Execution.TraceOptions"/>.
/// </summary>
/// <param name="Operators">When true, captures operator-pull spans.</param>
/// <param name="Scalars">When true, additionally captures per-scalar-call dispatches.</param>
public sealed record TraceOptionsJson(
    bool Operators = false,
    bool Scalars = false);

/// <summary>
/// JSON shape for a single <c>$name</c> binding. Exactly one of
/// <see cref="Value"/> / <see cref="Ref"/> is meaningful per parameter:
/// inline scalars use <see cref="Value"/>; binary parameters use
/// <see cref="Ref"/> to name a sibling multipart part whose bytes carry
/// the payload.
/// </summary>
/// <param name="Kind">The <c>DataKind</c> name (case-insensitive): Int32, String, Image, …</param>
/// <param name="Value">Inline JSON scalar — number, boolean, or string. Null for binary refs.</param>
/// <param name="Ref">Name of the sibling multipart part carrying the bytes; null for inline.</param>
public sealed record ParameterJson(
    string Kind,
    JsonElement? Value = null,
    string? Ref = null);
