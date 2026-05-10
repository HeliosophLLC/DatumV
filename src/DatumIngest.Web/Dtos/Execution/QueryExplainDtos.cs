namespace DatumIngest.Web.Dtos.Execution;

/// <summary>
/// Request payload for <c>POST /api/query/explain</c>.
/// </summary>
/// <param name="Sql">The SQL text to plan. Multi-statement batches are
/// supported — each statement is planned independently and the rendered
/// trees are concatenated with separator headers in the response.</param>
public sealed record QueryExplainRequest(string Sql);

/// <summary>
/// Response payload for <c>POST /api/query/explain</c>. Exactly one of
/// <see cref="Plan"/> or <see cref="Error"/> is non-null per response.
/// </summary>
/// <param name="Plan">Human-readable rendering of the EXPLAIN tree (operator
/// structure, cardinality estimates, pruning annotations, warnings). For
/// multi-statement batches, the trees are concatenated with
/// <c>── Statement N of M ──</c> separator headers, and per-statement
/// planning failures appear inline as <c>[error] …</c> lines.</param>
/// <param name="Error">Batch-level (parse) failure message, if any.</param>
public sealed record QueryExplainResponse(string? Plan, string? Error);
