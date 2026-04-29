using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the v1 batch-projector fast path in <c>ProjectOperator</c>.
/// The compiler accepts pure all-<c>CopyOrdinal</c> projections (every
/// output column maps directly to a source column, no LET, no ASSERT, no
/// expression evaluation). Anything richer falls back to <c>ProjectAsync</c>.
/// </summary>
/// <remarks>
/// Strategy: compare results between batchable-shape queries and queries
/// that force fallback by introducing a computed column. Both should
/// produce identical rows for the columns they share.
/// </remarks>
public sealed class BatchProjectorTests : ServiceTestBase
{
    [Fact]
    public async Task FastPath_SimplePassThrough_Float32()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "name", "value"],
            [1f, "alpha", 100f],
            [2f, "beta",  200f],
            [3f, "gamma", 300f]);

        List<Row> rows = await ExecuteQueryAsync("SELECT id, name, value FROM data", catalog);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsFloat32());
        Assert.Equal("alpha", rows[0]["name"].AsString());
        Assert.Equal(100f, rows[0]["value"].AsFloat32());
        Assert.Equal(300f, rows[2]["value"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_ReorderedColumns()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["a", "b", "c"],
            [1f, 2f, 3f],
            [4f, 5f, 6f]);

        // Reverse the order — exercises non-identity sourceOrdinals.
        List<Row> rows = await ExecuteQueryAsync("SELECT c, b, a FROM data", catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3f, rows[0]["c"].AsFloat32());
        Assert.Equal(2f, rows[0]["b"].AsFloat32());
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(6f, rows[1]["c"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_SubsetOfColumns()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "name", "value", "score"],
            [1f, "alpha", 100f, 0.9f],
            [2f, "beta",  200f, 0.7f]);

        List<Row> rows = await ExecuteQueryAsync("SELECT id, score FROM data", catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["id"].AsFloat32());
        Assert.Equal(0.9f, rows[0]["score"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_NullValues_PassThroughUnchanged()
    {
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, null],
            [2f, 200f],
            [3f, null]);

        List<Row> rows = await ExecuteQueryAsync("SELECT id, value FROM data", catalog);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0]["value"].IsNull);
        Assert.Equal(200f, rows[1]["value"].AsFloat32());
        Assert.True(rows[2]["value"].IsNull);
    }

    [Fact]
    public async Task FastPath_LargeBatch_MultipleOutputBatches()
    {
        // 10K input rows × 50% selectivity (via WHERE) gives ~5K projected rows.
        // Tests the readyBatches drain path when the output batch fills mid-loop.
        object?[][] inputRows = new object?[10_000][];
        for (int i = 0; i < 10_000; i++)
        {
            inputRows[i] = [(float)i, (float)i];
        }
        TableCatalog catalog = CreateCatalog("data", ["id", "value"], inputRows);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id, value FROM data WHERE value >= 5000",
            catalog);

        Assert.Equal(5000, rows.Count);
        Assert.Equal(5000f, rows[0]["id"].AsFloat32());
        Assert.Equal(9999f, rows[^1]["id"].AsFloat32());
    }

    [Fact]
    public async Task Fallback_ComputedColumn_StillCorrect()
    {
        // `value + 1` is an Evaluate slot, which the v1 compiler rejects.
        // The fallback per-row path must still produce correct results.
        TableCatalog catalog = CreateCatalog(
            "data",
            ["id", "value"],
            [1f, 100f],
            [2f, 200f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id, value + 1 AS bumped FROM data",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.Equal(101f, rows[0]["bumped"].AsFloat32());
        Assert.Equal(201f, rows[1]["bumped"].AsFloat32());
    }

    [Fact]
    public async Task FastPath_StringValues_PreservedAcrossBatchBoundary()
    {
        // Strings can be arena-backed, so the projector's
        // DataValueRetention.Stabilize on the destination arena is the
        // load-bearing piece — without it, output strings would dangle once
        // the input batch is returned to the pool. Stress this with multiple
        // input batches (default batch size = 1024).
        object?[][] inputRows = new object?[3000][];
        for (int i = 0; i < 3000; i++)
        {
            inputRows[i] = [(float)i, $"row_{i:D5}"];
        }
        TableCatalog catalog = CreateCatalog("data", ["id", "name"], inputRows);

        List<Row> rows = await ExecuteQueryAsync("SELECT id, name FROM data", catalog);

        Assert.Equal(3000, rows.Count);
        Assert.Equal("row_00000", rows[0]["name"].AsString());
        Assert.Equal("row_01023", rows[1023]["name"].AsString());   // last row of batch 0
        Assert.Equal("row_01024", rows[1024]["name"].AsString());   // first row of batch 1
        Assert.Equal("row_02999", rows[^1]["name"].AsString());
    }
}
