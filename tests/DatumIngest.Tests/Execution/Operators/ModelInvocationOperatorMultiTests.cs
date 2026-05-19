using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Execution.Operators;

/// <summary>
/// Multi-invocation <see cref="ModelInvocationOperator"/> unit tests.
/// Each test wires a <see cref="MockOperator"/> as the source plus two
/// <see cref="FakeModel"/> instances registered in a <see cref="ModelCatalog"/>;
/// the operator dispatches them and the test asserts the resulting
/// columns. No real ONNX runtime is exercised — the focus is the
/// operator's column stitching, lease lifecycle, and per-invocation
/// evaluation against the in-progress output batch (for vertical chains).
/// Companion to <c>ModelInvocationTests</c>, which exercises the
/// single-invocation path planning end-to-end through the hoister.
/// </summary>
public sealed class ModelInvocationOperatorMultiTests : ServiceTestBase
{
    /// <summary>
    /// Records each dispatch's arguments + emits a configurable
    /// transformation so tests can assert the dispatch shape and the
    /// scattered output.
    /// </summary>
    private sealed class FakeModel : IModel
    {
        public required string Name { get; init; }
        public required Func<string, string> Transform { get; init; }
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public int DispatchCount { get; private set; }
        public List<int> RowsPerDispatch { get; } = [];

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => InferBatchAsync(inputs, overrides, types: null, cancellationToken);

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            TypeRegistry? types,
            CancellationToken cancellationToken)
        {
            DispatchCount++;
            RowsPerDispatch.Add(inputs.Count);

            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                string input = inputs[i][0].AsString();
                results[i] = ValueRef.FromString(Transform(input));
            }
            return Task.FromResult<IReadOnlyList<ValueRef>>(results);
        }
    }

    private static ModelCatalogEntry MakeEntry(string name, Func<string, string> transform)
    {
        FakeModel model = new() { Name = name, Transform = transform };
        return new ModelCatalogEntry(
            Name: name,
            Backend: "fake",
            RelativePath: null, // synthetic — no file, no calibration
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => model);
    }

    private (TableCatalog catalog, ModelCatalog models, FakeModel model)
        SetupCatalog(string tableName, string[] columns, object?[][] rows, string modelName, Func<string, string> transform)
    {
        TableCatalog catalog = CreateCatalog(tableName, columns, rows);
        ModelCatalog models = new();
        FakeModel fake = new() { Name = modelName, Transform = transform };
        models.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "fake",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => fake));
        catalog.Models = models;
        return (catalog, models, fake);
    }

    private FakeModel RegisterFake(ModelCatalog models, string name, Func<string, string> transform)
    {
        FakeModel fake = new() { Name = name, Transform = transform };
        models.Register(new ModelCatalogEntry(
            Name: name,
            Backend: "fake",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => fake));
        return fake;
    }

    [Fact]
    public async Task Constructor_EmptyInvocations_Throws()
    {
        TableCatalog catalog = CreateCatalog("t", ["text"], new object?[] { "x" });
        MockOperator source = CreateMockOperator(["text"], new object?[] { "x" });

        Assert.Throws<ArgumentException>(() =>
            new ModelInvocationOperator(source, []));
    }

    [Fact]
    public async Task Horizontal_TwoSiblingsOverSameSource_PopulatesBothColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["text"],
            new object?[] { "alpha" },
            new object?[] { "beta" },
            new object?[] { "gamma" });
        // Pin the dispatch count to one chunk per invocation so the
        // assertion below isn't fighting the engine default's per-row
        // chunking behaviour. Chunking itself is exercised by dedicated
        // PolicyDrivenChunking_* tests below.
        ModelCatalog models = new() { BatchSizePolicy = StaticBatchSizePolicy.Instance };
        FakeModel upper = RegisterFake(models, "upper", s => s.ToUpperInvariant());
        FakeModel reverse = RegisterFake(models, "reverse", s => new string(s.Reverse().ToArray()));
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"], new object?[] { "alpha" }, new object?[] { "beta" }, new object?[] { "gamma" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation(
                "upper",
                [new ColumnReference("text")],
                [],
                "__model_upper"),
            new ModelInvocationOperator.Invocation(
                "reverse",
                [new ColumnReference("text")],
                [],
                "__model_reverse"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        List<Row> rows = await op.CollectRowsAsync(ctx);

        Assert.Equal(3, rows.Count);
        Assert.Equal("alpha", rows[0]["text"].AsString());
        Assert.Equal("ALPHA", rows[0]["__model_upper"].AsString());
        Assert.Equal("ahpla", rows[0]["__model_reverse"].AsString());
        Assert.Equal("BETA", rows[1]["__model_upper"].AsString());
        Assert.Equal("ateb", rows[1]["__model_reverse"].AsString());

        // Each model dispatched once per upstream batch (here the source
        // emits one batch).
        Assert.Equal(1, upper.DispatchCount);
        Assert.Equal(1, reverse.DispatchCount);
        Assert.Equal([3], upper.RowsPerDispatch);
        Assert.Equal([3], reverse.RowsPerDispatch);
    }

    [Fact]
    public async Task Vertical_ChainedInvocations_SecondReadsFirstsOutput()
    {
        // model_b's input expression references model_a's output column —
        // exercises the "evaluate inputs against the in-progress output
        // batch" mechanism that's the load-bearing detail of the
        // operator design.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["text"],
            new object?[] { "hello" },
            new object?[] { "world" });
        // Same single-chunk pin as the horizontal test.
        ModelCatalog models = new() { BatchSizePolicy = StaticBatchSizePolicy.Instance };
        RegisterFake(models, "upper", s => s.ToUpperInvariant());
        FakeModel suffix = RegisterFake(models, "suffix", s => s + "!");
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"], new object?[] { "hello" }, new object?[] { "world" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation(
                "upper",
                [new ColumnReference("text")],
                [],
                "__model_a"),
            new ModelInvocationOperator.Invocation(
                "suffix",
                // The KEY assertion: this input references __model_a, which
                // doesn't exist on the source batch. It must resolve against
                // the working output batch where model_a has just written.
                [new ColumnReference("__model_a")],
                [],
                "__model_b"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        List<Row> rows = await op.CollectRowsAsync(ctx);

        Assert.Equal(2, rows.Count);
        Assert.Equal("HELLO", rows[0]["__model_a"].AsString());
        Assert.Equal("HELLO!", rows[0]["__model_b"].AsString());
        Assert.Equal("WORLD!", rows[1]["__model_b"].AsString());

        // Suffix ran on the UPPERCASED inputs, proving it consumed
        // model_a's column from the in-progress output batch.
        Assert.Equal([2], suffix.RowsPerDispatch);
    }

    [Fact]
    public async Task OrderedExecution_InvocationsRunInSuppliedOrder()
    {
        // Order matters for vertical chains. This test makes sure
        // invocation 0 always runs before invocation 1, even when
        // invocation 1's expressions don't depend on invocation 0.
        TableCatalog catalog = CreateCatalog("t", ["text"], new object?[] { "x" });
        ModelCatalog models = new();
        List<string> order = [];

        FakeModel first = new() { Name = "first", Transform = s => s };
        FakeModel second = new() { Name = "second", Transform = s => s };

        // Custom registration that records execution order.
        models.Register(new ModelCatalogEntry(
            Name: "first", Backend: "fake", RelativePath: null,
            InputKinds: [DataKind.String], OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new OrderRecordingModel("first", order, s => s)));
        models.Register(new ModelCatalogEntry(
            Name: "second", Backend: "fake", RelativePath: null,
            InputKinds: [DataKind.String], OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => new OrderRecordingModel("second", order, s => s)));
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"], new object?[] { "x" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation("first",
                [new ColumnReference("text")], [], "__a"),
            new ModelInvocationOperator.Invocation("second",
                [new ColumnReference("text")], [], "__b"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        await op.CollectRowsAsync(ctx);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task PolicyDrivenChunking_DispatchesMultipleTimesPerInvocation()
    {
        // 7 rows + the default BatchOnePolicy → each invocation should
        // dispatch 7 times (one row per chunk). Asserts the chunking
        // loop is active, not bypassed.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["text"],
            new object?[] { "a" }, new object?[] { "b" }, new object?[] { "c" },
            new object?[] { "d" }, new object?[] { "e" }, new object?[] { "f" },
            new object?[] { "g" });
        ModelCatalog models = new() { BatchSizePolicy = BatchOnePolicy.Instance };
        FakeModel echo = RegisterFake(models, "echo", s => s);
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"],
            new object?[] { "a" }, new object?[] { "b" }, new object?[] { "c" },
            new object?[] { "d" }, new object?[] { "e" }, new object?[] { "f" },
            new object?[] { "g" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation(
                "echo", [new ColumnReference("text")], [], "__out"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        List<Row> rows = await op.CollectRowsAsync(ctx);

        Assert.Equal(7, rows.Count);
        Assert.Equal(7, echo.DispatchCount);
        Assert.All(echo.RowsPerDispatch, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task PolicyChunking_StaticPolicy_GroupsRowsByPreferredBatchSize()
    {
        // 10 rows + StaticBatchSizePolicy + model.PreferredBatchSize=null →
        // single chunk of 10. Confirms the policy's answer is honoured
        // rather than capped by the operator.
        TableCatalog catalog = CreateCatalog("t", ["text"],
            new object?[] { "1" }, new object?[] { "2" }, new object?[] { "3" }, new object?[] { "4" },
            new object?[] { "5" }, new object?[] { "6" }, new object?[] { "7" }, new object?[] { "8" },
            new object?[] { "9" }, new object?[] { "10" });
        ModelCatalog models = new() { BatchSizePolicy = StaticBatchSizePolicy.Instance };
        FakeModel echo = RegisterFake(models, "echo", s => s);
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"],
            new object?[] { "1" }, new object?[] { "2" }, new object?[] { "3" }, new object?[] { "4" },
            new object?[] { "5" }, new object?[] { "6" }, new object?[] { "7" }, new object?[] { "8" },
            new object?[] { "9" }, new object?[] { "10" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation(
                "echo", [new ColumnReference("text")], [], "__out"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        await op.CollectRowsAsync(ctx);

        Assert.Equal(1, echo.DispatchCount);
        Assert.Equal([10], echo.RowsPerDispatch);
    }

    [Fact]
    public async Task InputCountMismatch_ThrowsWithModelName()
    {
        TableCatalog catalog = CreateCatalog("t", ["text"], new object?[] { "x" });
        ModelCatalog models = new();
        RegisterFake(models, "needs_one", s => s);
        catalog.Models = models;

        MockOperator source = CreateMockOperator(["text"], new object?[] { "x" });
        ModelInvocationOperator op = new(source,
        [
            new ModelInvocationOperator.Invocation("needs_one",
                // Two expressions for a 1-input model.
                [new ColumnReference("text"), new ColumnReference("text")],
                [], "__out"),
        ]);

        using Heliosoph.DatumV.Execution.ExecutionContext ctx = CreateExecutionContext(catalog: catalog);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => op.CollectRowsAsync(ctx));
        Assert.Contains("needs_one", ex.Message);
    }

    private sealed class OrderRecordingModel : IModel
    {
        private readonly string _name;
        private readonly List<string> _order;
        private readonly Func<string, string> _transform;

        public OrderRecordingModel(string name, List<string> order, Func<string, string> transform)
        {
            _name = name;
            _order = order;
            _transform = transform;
        }

        public string Name => _name;
        public bool IsDeterministic => true;
        public IReadOnlyList<DataKind> InputKinds => [DataKind.String];
        public DataKind OutputKind => DataKind.String;

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            CancellationToken cancellationToken)
            => InferBatchAsync(inputs, overrides, types: null, cancellationToken);

        public Task<IReadOnlyList<ValueRef>> InferBatchAsync(
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
            TypeRegistry? types,
            CancellationToken cancellationToken)
        {
            _order.Add(_name);
            ValueRef[] results = new ValueRef[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
                results[i] = ValueRef.FromString(_transform(inputs[i][0].AsString()));
            return Task.FromResult<IReadOnlyList<ValueRef>>(results);
        }
    }
}
