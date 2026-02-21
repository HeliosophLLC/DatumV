namespace DatumIngest.Web.Dtos.Lsp;

/// <summary>
/// Request payload for SQL-position language endpoints (complete, hover,
/// signature). Cursor offset is a 0-based character index into the SQL
/// buffer, matching Monaco's model position-to-offset conversion.
/// </summary>
/// <param name="Sql">Full SQL text in the editor.</param>
/// <param name="Offset">0-based cursor offset within <paramref name="Sql"/>.</param>
public sealed record LangPositionRequest(string Sql, int Offset);

/// <summary>
/// Request payload for SQL-only endpoints (diagnose). No cursor offset —
/// diagnostics are computed over the whole document.
/// </summary>
/// <param name="Sql">SQL text to analyse.</param>
public sealed record LangSqlRequest(string Sql);
