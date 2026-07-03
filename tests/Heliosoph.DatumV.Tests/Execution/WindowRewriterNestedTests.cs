using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end coverage for window function calls nested inside composite
/// expressions — array literals (which lower to the <c>array</c> scalar
/// function) and scalar function arguments. The planner must hoist the
/// nested call into a <see cref="Heliosoph.DatumV.Execution.Operators.WindowOperator"/>
/// column and substitute a reference, exactly as it does for top-level calls.
/// </summary>
public sealed class WindowRewriterNestedTests : ServiceTestBase
{
    [Fact]
    public async Task WindowFunction_InsideArrayLiteral_IsHoisted()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0f],
            [2, 20.0f],
            [3, 30.0f]);

        // 3-row centered moving average packed into a 1-element array —
        // the shape a pose-smoothing query builds 12 of.
        List<double[]> result = await RunAsync(catalog,
            "SELECT id, [AVG(n) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING)]::Float64[] AS arr "
            + "FROM nums ORDER BY id",
            (row, arena) => row["arr"].AsArraySpan<double>(arena, null).ToArray());

        Assert.Equal(3, result.Count);
        Assert.Equal(15.0, result[0][0], 6);   // avg(10, 20)
        Assert.Equal(20.0, result[1][0], 6);   // avg(10, 20, 30)
        Assert.Equal(25.0, result[2][0], 6);   // avg(20, 30)
    }

    [Fact]
    public async Task WindowFunction_InsideScalarFunctionArgument_IsHoisted()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, -10.0],
            [2, -20.0]);

        List<double> result = await RunAsync(catalog,
            "SELECT id, abs(SUM(n) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)) AS a "
            + "FROM nums ORDER BY id",
            (row, _) => row["a"].AsFloat64());

        Assert.Equal([10.0, 30.0], result);
    }

    [Fact]
    public async Task DuplicateNestedWindowCalls_ShareOneColumn()
    {
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0],
            [2, 20.0]);

        // The same window expression appears twice inside one array — the
        // rewriter deduplicates by output name, so both elements must agree.
        List<double[]> result = await RunAsync(catalog,
            "SELECT id, [SUM(n) OVER (ORDER BY id), SUM(n) OVER (ORDER BY id)]::Float64[] AS arr "
            + "FROM nums ORDER BY id",
            (row, arena) => row["arr"].AsArraySpan<double>(arena, null).ToArray());

        Assert.Equal(2, result.Count);
        Assert.Equal(result[0][0], result[0][1]);
        Assert.Equal(result[1][0], result[1][1]);
        Assert.Equal(30.0, result[1][0], 6);
    }

    [Fact]
    public async Task PoseSmoothingShape_CastWindowElementsMixedWithConstants()
    {
        // The pose-smoothing idiom: window moving averages cast to Float32,
        // packed into one Float32[] literal alongside constant matrix entries.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0f],
            [2, 20.0f],
            [3, 30.0f]);

        List<float[]> result = await RunAsync(catalog,
            "SELECT id, ["
            + "(AVG(n) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING))::Float32, "
            + "0.0::Float32, 1.0::Float32"
            + "]::Float32[] AS pose_row FROM nums ORDER BY id",
            (row, arena) => row["pose_row"].AsArraySpan<float>(arena, null).ToArray());

        Assert.Equal(3, result.Count);
        Assert.Equal(15.0f, result[0][0], 4);
        Assert.Equal(20.0f, result[1][0], 4);
        Assert.Equal(25.0f, result[2][0], 4);
        Assert.Equal(0.0f, result[0][1]);
        Assert.Equal(1.0f, result[0][2]);
    }

    [Fact]
    public async Task WindowFunction_InsideAggregateArgument_StillRefused()
    {
        // A window value doesn't exist at aggregation time; the rewriter
        // deliberately leaves these alone so the evaluator's guard reports
        // them instead of silently mis-planning.
        TableCatalog catalog = CreateCatalog("nums",
            columns: ["id", "n"],
            [1, 10.0],
            [2, 20.0]);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync(catalog,
                "SELECT SUM(ROW_NUMBER() OVER (ORDER BY id)) AS s FROM nums",
                (row, _) => row["s"].AsInt64()));
    }

    private async Task<List<T>> RunAsync<T>(
        TableCatalog catalog, string sql, Func<Row, Arena, T> project)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<T> result = [];
        await foreach (RowBatch batch in ExecutePlanAsync(catalog, plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                result.Add(project(batch[i], batch.Arena));
            }
        }
        return result;
    }
}
