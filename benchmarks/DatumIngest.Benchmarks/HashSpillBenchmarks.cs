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
/// Targets the per-comparison hot path in set-membership operators (GroupBy,
/// Distinct, HashJoin, Intersect) under controlled spill conditions.
/// </summary>
/// <remarks>
/// <para>
/// The intent is to capture round-1 baseline numbers (existing on-demand
/// hashing) so the round-2 redesign (pre-stamped per-DataValue hash for
/// Image/Audio/arena Vector kinds) has something to compare against. Each
/// benchmark forces the GraceHash path by pinning
/// <see cref="ExecutionContext.MemoryBudgetBytes"/> to <see cref="SpillBudgetBytes"/>
/// — small enough that the 50K-row scenarios spill multiple partitions.
/// </para>
/// <para>
/// The Sidecar tiers (<see cref="KeyTier.SidecarLargeString"/>,
/// <see cref="KeyTier.SidecarImage"/>) and the collision-injection variants
/// are filed as follow-ups; the in-memory tiers (Inline / ArenaMedium /
/// ArenaVector) cover the most direct round-2 win for now.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class HashSpillBenchmarks
{
    /// <summary>
    /// Memory budget pinned on every <see cref="ExecutionContext"/> so the
    /// hash-join / group-by paths reliably take the Grace spill branch
    /// instead of falling back to the in-memory path under host RAM auto-sizing.
    /// </summary>
    private const long SpillBudgetBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Roughly 10% distinct keys → most rows hit existing groups in
    /// GroupBy / Distinct, which is exactly where the per-comparison cost
    /// dominates the benchmark.
    /// </summary>
    private const int DistinctKeyFraction = 10;

    [Params(KeyTier.InlineShortString, KeyTier.ArenaMediumString, KeyTier.ArenaVector)]
    public KeyTier Tier;

    [Params(50_000, 200_000)]
    public int RowCount;

    private Pool _pool = null!;
    private TableCatalog _singleTableCatalog = null!;
    private TableCatalog _joinTablesCatalog = null!;
    private string _singleTableName = null!;
    private string _leftTableName = null!;
    private string _rightTableName = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Pool(new PoolBacking());
        int distinct = Math.Max(2, RowCount / DistinctKeyFraction);

        (_singleTableCatalog, _singleTableName) =
            SpillScenarioFactory.BuildSingleTable(_pool, Tier, RowCount, distinct);

        (_joinTablesCatalog, _leftTableName, _rightTableName) =
            SpillScenarioFactory.BuildJoinTables(_pool, Tier, RowCount, distinct);
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

    [Benchmark(Description = "GROUP BY k")]
    public Task GroupBy() =>
        RunAsync(_singleTableCatalog, $"SELECT k, COUNT(*) FROM {_singleTableName} GROUP BY k");

    [Benchmark(Description = "SELECT DISTINCT k")]
    public Task Distinct() =>
        RunAsync(_singleTableCatalog, $"SELECT DISTINCT k FROM {_singleTableName}");

    [Benchmark(Description = "INNER JOIN ON k")]
    public Task HashJoin() =>
        RunAsync(_joinTablesCatalog,
            $"SELECT l.v FROM {_leftTableName} AS l INNER JOIN {_rightTableName} AS r ON l.k = r.k");

    [Benchmark(Description = "INTERSECT DISTINCT")]
    public Task Intersect() =>
        RunAsync(_joinTablesCatalog,
            $"SELECT k FROM {_leftTableName} INTERSECT SELECT k FROM {_rightTableName}");

    private async Task RunAsync(TableCatalog catalog, string sql)
    {
        FunctionRegistry functions = FunctionRegistry.CreateDefault();
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, functions);

        ExecutionContext context = catalog.CreateExecutionContext(
            memoryBudgetBytes: SpillBudgetBytes);

        QueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None)
            .ConfigureAwait(false);

        await foreach (RowBatch batch in plan.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.ReturnRowBatch(batch);
        }
    }
}
