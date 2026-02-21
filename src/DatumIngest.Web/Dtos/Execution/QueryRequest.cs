namespace DatumIngest.Web.Dtos.Execution;

/// <summary>
/// Request payload for <c>POST /api/query/stream</c>. The endpoint returns
/// NDJSON; this DTO carries only what's needed to kick off a batch.
/// Parameterised queries (`@name` binders) are deferred — drivers needing
/// them can splice values into the SQL until the parameter-binding port
/// lands.
/// </summary>
/// <param name="Sql">The SQL text to execute. Multi-statement batches accepted.</param>
/// <param name="MaxRows">Per-cell row cap; defaults to 1000 when omitted or non-positive.</param>
/// <param name="Trace">When true, emits a final <c>trace</c> event carrying the engine's diagnostic capture for the batch.</param>
public sealed record QueryStreamRequest(string Sql, int? MaxRows = null, bool? Trace = null);
