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
    public ActionResult<QueryExplainResponse> Explain([FromBody] QueryExplainRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            return BadRequest(new QueryExplainResponse(Plan: null, Error: "sql is required"));
        }

        IQueryPlan plan;
        try
        {
            // Planning is synchronous in practice (PlanAsync wraps a sync
            // body in Task.FromResult) — call .GetAwaiter().GetResult on
            // the returned Task to keep the controller signature
            // synchronous and avoid an async state machine for the
            // common case. Switch to async only if PlanAsync ever
            // becomes truly async.
            plan = catalog.PlanAsync(request.Sql).GetAwaiter().GetResult();
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
