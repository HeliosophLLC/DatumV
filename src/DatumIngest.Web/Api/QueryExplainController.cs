using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Web.Dtos.Execution;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

/// <summary>
/// EXPLAIN endpoint companion to <see cref="QueryStreamController"/>.
/// Plans the supplied SQL and returns the rendered static EXPLAIN tree —
/// no query execution. EXPLAIN ANALYZE is intentionally out of scope here;
/// add a follow-up endpoint when the UI wants runtime metrics.
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

        IQueryPlan plan;
        try
        {
            // PlanAsync is side-effect-free for DDL/DML (returns a deferred
            // plan that only runs the statement on iteration). EXPLAIN reads
            // ExplainTree without iterating, so `EXPLAIN DELETE FROM users`
            // never actually deletes — the deferral keeps this endpoint safe.
            // Planning is synchronous in practice (PlanAsync wraps a sync
            // body in Task.FromResult) — call .GetAwaiter().GetResult on the
            // returned Task to keep the controller signature synchronous.
            plan = await catalog.PlanAsync(request.Sql);
        }
        catch (Exception ex)
        {
            return Ok(new QueryExplainResponse(Plan: null, Error: ex.Message));
        }

        ExplainPlanNode tree;
        try
        {
            tree = plan.ExplainTree;
        }
        catch (Exception ex)
        {
            return Ok(new QueryExplainResponse(Plan: null, Error: ex.Message));
        }

        return Ok(new QueryExplainResponse(Plan: tree.Render(), Error: null));
    }
}
