using BenchmarkDotNet.Attributes;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Benchmarks;

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

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Pool(new PoolBacking());
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
            """);

    private async Task RunAsync(string sql)
    {
        TableCatalog catalog = new(_pool);
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);

        ExecutionContext context = new(
            CancellationToken.None,
            functions,
            catalog,
            _pool);

        QueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None)
            .ConfigureAwait(false);

        await foreach (RowBatch batch in plan.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.ReturnRowBatch(batch);
        }
    }
}
