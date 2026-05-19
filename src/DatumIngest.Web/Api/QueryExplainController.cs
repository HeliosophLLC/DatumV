using System.Text;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Web.Dtos.Execution;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// EXPLAIN endpoint companion to <see cref="QueryStreamController"/>.
/// Plans the supplied SQL and returns the rendered static EXPLAIN tree —
/// no query execution. Accepts a multi-statement batch; each statement
/// is planned independently and the rendered trees are concatenated with
/// separator headers. EXPLAIN ANALYZE is intentionally out of scope
/// here; add a follow-up endpoint when the UI wants runtime metrics.
/// </summary>
[ApiController]
[Route("api/query")]
public sealed class QueryExplainController(TableCatalog catalog) : ControllerBase
{
    [HttpPost("explain")]
    public async Task<ActionResult<QueryExplainResponse>> Explain([FromBody] QueryExplainRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            return BadRequest(new QueryExplainResponse(Plan: null, Error: "sql is required"));
        }

        IReadOnlyList<(Statement Statement, string SourceText)> statements;
        try
        {
            // Batch parse: supports `SELECT 1; SELECT 2; …`. A single
            // statement input yields a one-element list — the single-
            // statement render path falls out naturally below.
            statements = SqlParser.ParseBatchWithText(request.Sql);
        }
        catch (Exception ex)
        {
            return Ok(new QueryExplainResponse(Plan: null, Error: ex.Message));
        }

        if (statements.Count == 0)
        {
            return Ok(new QueryExplainResponse(Plan: null, Error: "no statements parsed"));
        }

        // Plan each statement and stitch their rendered trees. PlanAsync
        // is side-effect-free for every statement type (every plan only
        // applies side effects inside ExecuteImplAsync, never inside
        // ExplainTree), so iterating the rendered trees here is safe even
        // for batches containing DDL / DML.
        StringBuilder builder = new();
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements.Count > 1)
            {
                builder.Append("── Statement ").Append(i + 1).Append(" of ").Append(statements.Count).AppendLine(" ──");
            }
            try
            {
                StatementPlan plan = await catalog.PlanAsync(statements[i].Statement, statements[i].SourceText).ConfigureAwait(false);
                builder.Append(plan.ExplainTree.Render());
            }
            catch (Exception ex)
            {
                builder.Append("[error] ").AppendLine(ex.Message);
            }
            if (i + 1 < statements.Count) builder.AppendLine();
        }

        return Ok(new QueryExplainResponse(Plan: builder.ToString(), Error: null));
    }
}
