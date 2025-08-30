using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the columnar operator pipeline: scan, filter, project, limit, alias,
/// and the <see cref="ColumnBatchToRowBatchAdapter"/>.
/// </summary>
public sealed class ColumnBatchOperatorTests
{
    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Creates a <see cref="ColumnBatch"/> from named columns and row data.
    /// </summary>
    private static ColumnBatch MakeBatch(string[] columnNames, DataValue[][] rows)
    {
        int rowCount = rows.Length;
        int columnCount = columnNames.Length;
        ColumnBatch batch = ColumnBatch.Create(columnNames, rowCount);

        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                batch.SetValue(column, row, rows[row][column]);
            }
        }

        batch.SetRowCount(rowCount);
        return batch;
    }

    /// <summary>
    /// An in-memory columnar operator that yields pre-built batches.
    /// </summary>
    private sealed class InMemoryColumnBatchOperator : IColumnBatchOperator
    {
        private readonly ColumnBatch[] _batches;

        internal InMemoryColumnBatchOperator(params ColumnBatch[] batches)
        {
            _batches = batches;
        }

        public OperatorPlanDescription DescribeForExplain()
            => new("InMemory") { Properties = new Dictionary<string, string> { ["mode"] = "columnar" } };

#pragma warning disable CS1998 // Async method lacks 'await' operators
        public async IAsyncEnumerable<ColumnBatch> ExecuteColumnBatchAsync(ExecutionContext context)
        {
            foreach (ColumnBatch batch in _batches)
            {
                yield return batch;
            }
        }
#pragma warning restore CS1998
    }

    /// <summary>
    /// Collects all column batches from an operator into a flat list of rows.
    /// Each row is represented as a <see cref="DataValue"/> array.
    /// </summary>
    private static async Task<List<DataValue[]>> CollectRowsAsync(
        IColumnBatchOperator source, ExecutionContext context)
    {
        List<DataValue[]> rows = [];
        await foreach (ColumnBatch batch in source.ExecuteColumnBatchAsync(context))
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                DataValue[] values = new DataValue[batch.ColumnCount];
                for (int column = 0; column < batch.ColumnCount; column++)
                {
                    values[column] = batch.GetValue(row, column);
                }

                rows.Add(values);
            }

            batch.Dispose();
        }

        return rows;
    }

    // ───────────────────────── Filter ─────────────────────────

    [Fact]
    public async Task FilterKeepsMatchingRows()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "value"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(10)],
                [DataValue.FromInt32(2), DataValue.FromInt32(20)],
                [DataValue.FromInt32(3), DataValue.FromInt32(30)],
                [DataValue.FromInt32(4), DataValue.FromInt32(40)],
            ]);

        // WHERE value > 15
        Expression predicate = new BinaryExpression(
            new ColumnReference("value"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(15));

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchFilterOperator filter = new(source, predicate);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(filter, context);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataValue.FromInt32(2), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(3), rows[1][0]);
        Assert.Equal(DataValue.FromInt32(4), rows[2][0]);
    }

    [Fact]
    public async Task FilterDiscardsAllWhenNoneMatch()
    {
        ColumnBatch batch = MakeBatch(
            ["x"],
            [
                [DataValue.FromInt32(1)],
                [DataValue.FromInt32(2)],
            ]);

        // WHERE x > 100
        Expression predicate = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(100));

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchFilterOperator filter = new(source, predicate);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(filter, context);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task FilterYieldsUnchangedWhenAllMatch()
    {
        ColumnBatch batch = MakeBatch(
            ["x"],
            [
                [DataValue.FromInt32(10)],
                [DataValue.FromInt32(20)],
            ]);

        // WHERE x > 0  — all match
        Expression predicate = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(0));

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchFilterOperator filter = new(source, predicate);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(filter, context);

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataValue.FromInt32(10), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(20), rows[1][0]);
    }

    // ───────────────────────── Limit ─────────────────────────

    [Fact]
    public async Task LimitTruncatesRows()
    {
        ColumnBatch batch = MakeBatch(
            ["id"],
            [
                [DataValue.FromInt32(1)],
                [DataValue.FromInt32(2)],
                [DataValue.FromInt32(3)],
                [DataValue.FromInt32(4)],
                [DataValue.FromInt32(5)],
            ]);

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchLimitOperator limit = new(source, limit: 3);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(limit, context);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataValue.FromInt32(1), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(2), rows[1][0]);
        Assert.Equal(DataValue.FromInt32(3), rows[2][0]);
    }

    [Fact]
    public async Task LimitWithOffsetSkipsRows()
    {
        ColumnBatch batch = MakeBatch(
            ["id"],
            [
                [DataValue.FromInt32(1)],
                [DataValue.FromInt32(2)],
                [DataValue.FromInt32(3)],
                [DataValue.FromInt32(4)],
                [DataValue.FromInt32(5)],
            ]);

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchLimitOperator limit = new(source, limit: 2, offset: 2);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(limit, context);

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataValue.FromInt32(3), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(4), rows[1][0]);
    }

    [Fact]
    public async Task LimitAcrossMultipleBatches()
    {
        ColumnBatch batch1 = MakeBatch(["x"], [
            [DataValue.FromInt32(1)],
            [DataValue.FromInt32(2)],
        ]);

        ColumnBatch batch2 = MakeBatch(["x"], [
            [DataValue.FromInt32(3)],
            [DataValue.FromInt32(4)],
        ]);

        InMemoryColumnBatchOperator source = new(batch1, batch2);
        ColumnBatchLimitOperator limit = new(source, limit: 3);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(limit, context);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DataValue.FromInt32(1), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(2), rows[1][0]);
        Assert.Equal(DataValue.FromInt32(3), rows[2][0]);
    }

    [Fact]
    public async Task LimitWithOffsetAcrossBatches()
    {
        ColumnBatch batch1 = MakeBatch(["x"], [
            [DataValue.FromInt32(1)],
            [DataValue.FromInt32(2)],
        ]);

        ColumnBatch batch2 = MakeBatch(["x"], [
            [DataValue.FromInt32(3)],
            [DataValue.FromInt32(4)],
        ]);

        ColumnBatch batch3 = MakeBatch(["x"], [
            [DataValue.FromInt32(5)],
        ]);

        InMemoryColumnBatchOperator source = new(batch1, batch2, batch3);
        // offset=3, limit=2 → skip 1,2,3 → take 4,5
        ColumnBatchLimitOperator limit = new(source, limit: 2, offset: 3);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(limit, context);

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataValue.FromInt32(4), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(5), rows[1][0]);
    }

    // ───────────────────────── Alias ─────────────────────────

    [Fact]
    public async Task AliasPrefixesColumnNames()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "name"],
            [
                [DataValue.FromInt32(1), DataValue.FromString("Alice")],
            ]);

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchAliasOperator alias = new(source, "t");

        ExecutionContext context = TestExecutionContext.Create();

        await foreach (ColumnBatch outputBatch in alias.ExecuteColumnBatchAsync(context))
        {
            Assert.Equal("t.id", outputBatch.GetColumnName(0));
            Assert.Equal("t.name", outputBatch.GetColumnName(1));

            // Both qualified and unqualified lookups should resolve.
            Assert.True(outputBatch.TryGetColumnOrdinal("t.id", out int qualified));
            Assert.Equal(0, qualified);
            Assert.True(outputBatch.TryGetColumnOrdinal("id", out int unqualified));
            Assert.Equal(0, unqualified);

            Assert.Equal(DataValue.FromInt32(1), outputBatch.GetValue(0, 0));
            Assert.Equal(DataValue.FromString("Alice"), outputBatch.GetValue(0, 1));

            outputBatch.Dispose();
        }
    }

    // ───────────────────────── Project ─────────────────────────

    [Fact]
    public async Task ProjectSelectsSubsetOfColumns()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "name", "age"],
            [
                [DataValue.FromInt32(1), DataValue.FromString("Alice"), DataValue.FromInt32(30)],
                [DataValue.FromInt32(2), DataValue.FromString("Bob"), DataValue.FromInt32(25)],
            ]);

        // SELECT name, age
        SelectColumn[] columns =
        [
            new SelectColumn(new ColumnReference("name")),
            new SelectColumn(new ColumnReference("age")),
        ];

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchProjectOperator project = new(source, columns);

        ExecutionContext context = TestExecutionContext.Create();

        await foreach (ColumnBatch outputBatch in project.ExecuteColumnBatchAsync(context))
        {
            Assert.Equal(2, outputBatch.ColumnCount);
            Assert.Equal(2, outputBatch.RowCount);
            Assert.Equal("name", outputBatch.GetColumnName(0));
            Assert.Equal("age", outputBatch.GetColumnName(1));
            Assert.Equal(DataValue.FromString("Alice"), outputBatch.GetValue(0, 0));
            Assert.Equal(DataValue.FromInt32(30), outputBatch.GetValue(0, 1));
            Assert.Equal(DataValue.FromString("Bob"), outputBatch.GetValue(1, 0));
            Assert.Equal(DataValue.FromInt32(25), outputBatch.GetValue(1, 1));

            outputBatch.Dispose();
        }
    }

    [Fact]
    public async Task ProjectWithAlias()
    {
        ColumnBatch batch = MakeBatch(
            ["value"],
            [
                [DataValue.FromInt32(42)],
            ]);

        // SELECT value AS result
        SelectColumn[] columns =
        [
            new SelectColumn(new ColumnReference("value"), Alias: "result"),
        ];

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchProjectOperator project = new(source, columns);

        ExecutionContext context = TestExecutionContext.Create();

        await foreach (ColumnBatch outputBatch in project.ExecuteColumnBatchAsync(context))
        {
            Assert.Single(outputBatch.ColumnNames);
            Assert.Equal("result", outputBatch.GetColumnName(0));
            Assert.Equal(DataValue.FromInt32(42), outputBatch.GetValue(0, 0));

            outputBatch.Dispose();
        }
    }

    [Fact]
    public async Task ProjectSelectStar()
    {
        ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(2)],
            ]);

        // SELECT *
        SelectColumn[] columns = [new SelectAllColumns()];

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchProjectOperator project = new(source, columns);

        ExecutionContext context = TestExecutionContext.Create();

        await foreach (ColumnBatch outputBatch in project.ExecuteColumnBatchAsync(context))
        {
            Assert.Equal(2, outputBatch.ColumnCount);
            Assert.Equal("a", outputBatch.GetColumnName(0));
            Assert.Equal("b", outputBatch.GetColumnName(1));
            Assert.Equal(DataValue.FromInt32(1), outputBatch.GetValue(0, 0));
            Assert.Equal(DataValue.FromInt32(2), outputBatch.GetValue(0, 1));

            outputBatch.Dispose();
        }
    }

    [Fact]
    public async Task ProjectEvaluatesExpression()
    {
        ColumnBatch batch = MakeBatch(
            ["x"],
            [
                [DataValue.FromInt32(10)],
                [DataValue.FromInt32(20)],
            ]);

        // SELECT x * 2 AS doubled
        SelectColumn[] columns =
        [
            new SelectColumn(
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.Multiply,
                    new LiteralExpression(2)),
                Alias: "doubled"),
        ];

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchProjectOperator project = new(source, columns);

        ExecutionContext context = TestExecutionContext.Create();

        await foreach (ColumnBatch outputBatch in project.ExecuteColumnBatchAsync(context))
        {
            Assert.Equal("doubled", outputBatch.GetColumnName(0));
            Assert.Equal(20.0f, outputBatch.GetValue(0, 0).AsFloat32());
            Assert.Equal(40.0f, outputBatch.GetValue(1, 0).AsFloat32());

            outputBatch.Dispose();
        }
    }

    // ───────────────────────── Adapter ─────────────────────────

    [Fact]
    public async Task AdapterConvertsColumnBatchToRowBatch()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "name"],
            [
                [DataValue.FromInt32(1), DataValue.FromString("Alice")],
                [DataValue.FromInt32(2), DataValue.FromString("Bob")],
            ]);

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchToRowBatchAdapter adapter = new(source);

        ExecutionContext context = TestExecutionContext.Create();
        List<Row> rows = [];

        await foreach (RowBatch rowBatch in adapter.ExecuteAsync(context))
        {
            for (int i = 0; i < rowBatch.Count; i++)
            {
                rows.Add(rowBatch[i]);
            }

            rowBatch.Return();
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(DataValue.FromInt32(1), rows[0][0]);
        Assert.Equal(DataValue.FromString("Alice"), rows[0][1]);
        Assert.Equal(DataValue.FromInt32(2), rows[1][0]);
        Assert.Equal(DataValue.FromString("Bob"), rows[1][1]);
    }

    // ───────────────────────── Pipeline ─────────────────────────

    [Fact]
    public async Task FullPipelineFilterProjectLimit()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "score"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(50)],
                [DataValue.FromInt32(2), DataValue.FromInt32(80)],
                [DataValue.FromInt32(3), DataValue.FromInt32(90)],
                [DataValue.FromInt32(4), DataValue.FromInt32(70)],
                [DataValue.FromInt32(5), DataValue.FromInt32(95)],
            ]);

        // SELECT id FROM data WHERE score >= 80 LIMIT 2
        InMemoryColumnBatchOperator scan = new(batch);

        ColumnBatchFilterOperator filter = new(scan,
            new BinaryExpression(
                new ColumnReference("score"),
                BinaryOperator.GreaterThanOrEqual,
                new LiteralExpression(80)));

        ColumnBatchProjectOperator project = new(filter, [
            new SelectColumn(new ColumnReference("id")),
        ]);

        ColumnBatchLimitOperator limit = new(project, limit: 2);

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(limit, context);

        Assert.Equal(2, rows.Count);
        // Rows with score >= 80: id=2 (80), id=3 (90), id=4 (70-skip), id=5 (95)
        // First 2 matching: id=2, id=3
        Assert.Equal(DataValue.FromInt32(2), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(3), rows[1][0]);
    }

    [Fact]
    public async Task FullPipelineAliasThenFilter()
    {
        ColumnBatch batch = MakeBatch(
            ["id", "value"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(100)],
                [DataValue.FromInt32(2), DataValue.FromInt32(200)],
            ]);

        InMemoryColumnBatchOperator scan = new(batch);
        ColumnBatchAliasOperator alias = new(scan, "t");

        // WHERE t.value > 150
        ColumnBatchFilterOperator filter = new(alias,
            new BinaryExpression(
                new ColumnReference("t", "value"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(150)));

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(filter, context);

        Assert.Single(rows);
        Assert.Equal(DataValue.FromInt32(2), rows[0][0]);
        Assert.Equal(DataValue.FromInt32(200), rows[0][1]);
    }

    [Fact]
    public async Task EmptyBatchProducesNoOutput()
    {
        ColumnBatch batch = ColumnBatch.Create(["x"], 0);
        batch.SetRowCount(0);

        InMemoryColumnBatchOperator source = new(batch);
        ColumnBatchFilterOperator filter = new(source,
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        ExecutionContext context = TestExecutionContext.Create();
        List<DataValue[]> rows = await CollectRowsAsync(filter, context);

        Assert.Empty(rows);
    }

    // ───────────────────────── Describe for EXPLAIN ─────────────────────────

    [Fact]
    public void FilterDescribeContainsPredicate()
    {
        InMemoryColumnBatchOperator source = new();
        ColumnBatchFilterOperator filter = new(source,
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.Equal,
                new LiteralExpression(1)));

        OperatorPlanDescription description = filter.DescribeForExplain();

        Assert.Equal("Filter", description.OperatorName);
        Assert.Equal("columnar", description.Properties!["mode"]);
    }

    [Fact]
    public void LimitDescribeShowsLimit()
    {
        InMemoryColumnBatchOperator source = new();
        ColumnBatchLimitOperator limit = new(source, limit: 42, offset: 10);

        OperatorPlanDescription description = limit.DescribeForExplain();

        Assert.Equal("Limit", description.OperatorName);
        Assert.Equal("42", description.Properties!["limit"]);
        Assert.Equal("10", description.Properties["offset"]);
    }

    [Fact]
    public void AliasDescribeShowsAlias()
    {
        InMemoryColumnBatchOperator source = new();
        ColumnBatchAliasOperator alias = new(source, "my_table");

        OperatorPlanDescription description = alias.DescribeForExplain();

        Assert.Equal("Alias", description.OperatorName);
        Assert.Equal("my_table", description.Properties!["alias"]);
    }

    [Fact]
    public void AdapterDescribeDelegatesToSource()
    {
        InMemoryColumnBatchOperator source = new();
        ColumnBatchToRowBatchAdapter adapter = new(source);

        OperatorPlanDescription description = adapter.DescribeForExplain();

        // Adapter is transparent — delegates to source.
        Assert.Equal("InMemory", description.OperatorName);
    }
}
