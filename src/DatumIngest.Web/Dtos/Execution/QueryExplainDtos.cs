namespace DatumIngest.Web.Dtos.Execution;

/// <summary>
/// Request payload for <c>POST /api/query/explain</c>.
/// </summary>
/// <param name="Sql">The SQL text to plan. A single statement is expected;
/// any additional statements in the input are ignored by the parser the same
/// way <see cref="DatumIngest.Catalog.TableCatalog.PlanAsync(string)"/> handles them.</param>
public sealed record QueryExplainRequest(string Sql);

/// <summary>
/// Response payload for <c>POST /api/query/explain</c>. Exactly one of
/// <see cref="Plan"/> or <see cref="Error"/> is non-null per response.
/// </summary>
/// <param name="Plan">Human-readable rendering of the EXPLAIN tree
/// (operator structure, cardinality estimates, pruning annotations, warnings).</param>
/// <param name="Error">Parse / planning failure message, if any.</param>
public sealed record QueryExplainResponse(string? Plan, string? Error);
