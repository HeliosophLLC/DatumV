using BenchmarkDotNet.Attributes;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Benchmarks;

/// <summary>
/// Benchmarks for recursive-CTE re-materialisation. Split out from the
/// hash-spill benchmarks because the per-iteration cost has a different
/// shape: each recursive step re-emits the working table through a stabilise
/// pass, so the cost we want to measure is per-row arena stabilisation
/// rather than per-comparison equality.
/// </summary>
/// <remarks>
/// <para>
/// Round 2 of the hash-index spill redesign should reduce the stabilisation
/// cost for non-string arena values (Vector / byte-arrays / media) by
/// stamping a hash at value-construction time — recursive iterations that
/// re-stabilise into a new arena can then skip the byte copy when the hash
/// already matches.
/// </para>
/// <para>
/// Vanilla (Int32 anchor + Int32 step) is implemented; a sidecar-valued
/// recursive CTE variant is filed as a follow-up alongside the sidecar
/// tiers in <see cref="HashSpillBenchmarks"/>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CteRematerializationBenchmarks
{
    /// <summary>
    /// Number of recursive iterations. 100 is the lightweight smoke; 1000 is
    /// the steady-state cost dominator (matches the existing 1K-iteration
    /// benchmark from the stale ExecutionBenchmarks file).
    /// </summary>
    [Params(100, 1000)]
    public int Iterations;

    private Pool _pool = null!;
    private TableCatalog _heavyCatalog = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Pool(new PoolBacking());

        // Source table for the materialization-gating benchmarks: 5K rows of
        // (id, name). The heavy CTE chains md5 calls per row, so its cost is
        // proportional to N rows × M re-evaluations. With the recursive walker
        // forcing 21 references (1 seed + 20 recursive joins), the bug case
        // does ~21× more md5 work than the materialized case.
        object?[][] rows = new object?[5000][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = [i + 1, "row-" + (i + 1).ToString("D6")];
        }
        _heavyCatalog = new TableCatalog(_pool);
        _heavyCatalog.Add(new InMemoryTableProvider(_pool, "source", ["id", "name"], rows));
    }

    [IterationSetup]
    public void ResetCounters()
    {
        HashGateStats.Reset();
        HashGateStats.Enabled = true;
    }

    [IterationCleanup]
    public void StopCounters()
    {
        HashGateStats.Enabled = false;
    }

    /// <summary>
    /// Pure Int32 recursion — baseline shape with no value-stabilisation cost
    /// (Int32 is inline). Establishes a floor for the recursion-loop overhead
    /// itself so the round-2 sidecar-CTE benchmark numbers can be diffed
    /// against a known-cheap value kind.
    /// </summary>
    [Benchmark(Description = "WITH RECURSIVE counter")]
    public Task RecursiveCounter() =>
        RunAsync(
            $$"""
            WITH RECURSIVE counter(n) AS (
                SELECT 1
                UNION ALL
                SELECT n + 1 FROM counter WHERE n < {{Iterations}}
            )
            SELECT n FROM counter
            """,
            new TableCatalog(_pool));

    // ─── CTE materialization-gating benchmarks ───────────────────────────────
    //
    // Each pair runs the SAME query twice, only differing on whether the
    // sibling `heavy` CTE carries an explicit MATERIALIZED hint. The hint
    // bypasses CountCommonTableExpressionReferences (which only walks the
    // outer SELECT's FROM/JOINs, missing references inside other CTE bodies
    // and recursive parts), so the gap measures the cost of the
    // reference-counting bug: every recursive iteration re-runs the inlined
    // `heavy` plan in full.
    //
    // Expected today: ~20× gap (1 source pass vs 21 source passes).
    // Expected after fix: gap collapses to noise.

    private const int RecursiveCteIterations = 20;

    // 10 chained md5 calls per row — each one hashes the previous result,
    // making the per-row cost large enough that 21× re-evaluation is clearly
    // visible against the timer.
    private const string HeavyPayloadExpr =
        "md5(md5(md5(md5(md5(md5(md5(md5(md5(md5(name))))))))))";

    private static readonly string MaterializedSql = $$"""
        WITH RECURSIVE
        heavy AS MATERIALIZED (
            SELECT id, {{HeavyPayloadExpr}} AS payload FROM source
        ),
        walker AS (
            SELECT id, payload FROM heavy WHERE id = 1
            UNION ALL
            SELECT s.id, s.payload
            FROM walker w
            JOIN heavy s ON s.id = w.id + 1
            WHERE s.id <= {{RecursiveCteIterations}}
        )
        SELECT id FROM walker
        """;

    private static readonly string DefaultSql = $$"""
        WITH RECURSIVE
        heavy AS (
            SELECT id, {{HeavyPayloadExpr}} AS payload FROM source
        ),
        walker AS (
            SELECT id, payload FROM heavy WHERE id = 1
            UNION ALL
            SELECT s.id, s.payload
            FROM walker w
            JOIN heavy s ON s.id = w.id + 1
            WHERE s.id <= {{RecursiveCteIterations}}
        )
        SELECT id FROM walker
        """;

    [Benchmark(Description = "Recursive CTE w/ explicit MATERIALIZED on sibling")]
    public Task RecursiveCte_MaterializedHint() => RunAsync(MaterializedSql, _heavyCatalog);

    [Benchmark(Description = "Recursive CTE w/ default materialization gating")]
    public Task RecursiveCte_DefaultGating() => RunAsync(DefaultSql, _heavyCatalog);

    // Expression-subquery CTE references (IN / EXISTS / scalar subquery)
    // are currently rejected outright by the engine:
    //   - IN  + CTE  → "IN (SELECT ...) was not rewritten by the query planner into a semi-join"
    //   - EXISTS + CTE → same rewriter error
    //   - scalar + CTE → SchemaResolutionException: CTE name not in scope
    // Once those three paths are unblocked, the walker in
    // QueryPlanner.CountReferences* will need an Expression-walking arm
    // (currently it only walks FROM + JOIN sources). Filing a benchmark
    // here is premature — there's no working query to measure.

    private async Task RunAsync(string sql, TableCatalog catalog)
    {
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);

        ExecutionContext context = catalog.CreateExecutionContext();

        QueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None)
            .ConfigureAwait(false);

        await foreach (RowBatch batch in plan.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.ReturnRowBatch(batch);
        }
    }
}
