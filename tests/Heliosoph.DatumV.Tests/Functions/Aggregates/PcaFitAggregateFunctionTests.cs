using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="PcaFitAggregateFunction"/> — <c>pca_fit_agg(vec, k)</c>.
/// Verifies the model-struct contract (mean / components / variance_ratio
/// fields with a registry-resolvable TypeId), the eigenstructure on data with
/// known principal axes, the deterministic sign convention, null / dimension /
/// arity error handling, and the aggregate lifecycle (Merge equivalence,
/// Reset reuse).
/// </summary>
public sealed class PcaFitAggregateFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pcafit_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// Four points whose sample covariance is diag(6, 2/3): variance 6 along x,
    /// 2/3 along y. PC1 = (1,0), PC2 = (0,1); variance_ratio = (0.9, 0.1).
    /// </summary>
    private static readonly float[][] AxisAlignedPoints =
    [
        [3f, 0f],
        [-3f, 0f],
        [0f, 1f],
        [0f, -1f],
    ];

    private static DataValue Vec(Arena arena, params float[] values) =>
        DataValue.FromArenaArray<float>(values, DataKind.Float32, arena);

    private static void AccumulatePoints(
        IAggregateAccumulator acc, Arena arena, in InvocationFrame frame, int k, IEnumerable<float[]> points)
    {
        foreach (float[] p in points)
        {
            acc.Accumulate([Vec(arena, p), DataValue.FromInt32(k)], frame);
        }
    }

    private (Arena Arena, TypeRegistry Types, InvocationFrame Frame) CreateFitContext()
    {
        Arena arena = CreateArena();
        TypeRegistry types = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena, null, types);
        return (arena, types, frame);
    }

    private static (float[] Mean, float[] Components, float[] VarianceRatio, int[] Shape) ReadModel(
        DataValue result, Arena arena, TypeRegistry types)
    {
        Assert.Equal(DataKind.Struct, result.Kind);
        Assert.False(result.IsNull);
        Assert.NotEqual(0, (int)result.TypeId);

        TypeDescriptor? desc = types.GetDescriptor(result.TypeId);
        Assert.NotNull(desc?.Fields);
        int meanIdx = desc!.FindFieldIndex("mean");
        int compIdx = desc.FindFieldIndex("components");
        int ratioIdx = desc.FindFieldIndex("variance_ratio");
        Assert.True(meanIdx >= 0, "model struct must carry a 'mean' field");
        Assert.True(compIdx >= 0, "model struct must carry a 'components' field");
        Assert.True(ratioIdx >= 0, "model struct must carry a 'variance_ratio' field");

        DataValue[] fields = result.AsStruct(arena);
        Assert.True(fields[compIdx].IsMultiDim, "components must be a multi-dim [k, d] array");
        return (
            fields[meanIdx].AsArraySpan<float>(arena).ToArray(),
            fields[compIdx].AsArraySpan<float>(arena).ToArray(),
            fields[ratioIdx].AsArraySpan<float>(arena).ToArray(),
            fields[compIdx].GetShape(arena).ToArray());
    }

    /// <summary>
    /// Asserts two unit vectors span the same axis with the sign convention
    /// already applied to <paramref name="actual"/> — i.e. exact component
    /// match, not match-up-to-sign.
    /// </summary>
    private static void AssertComponentEquals(float[] expected, ReadOnlySpan<float> actual, float tol = 1e-3f)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                System.Math.Abs(expected[i] - actual[i]) <= tol,
                $"component[{i}]: expected {expected[i]}, got {actual[i]}");
        }
    }

    // ───────────────────────── metadata ─────────────────────────

    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("pca_fit_agg", PcaFitAggregateFunction.Name);
        Assert.Equal(FunctionCategory.Aggregate, PcaFitAggregateFunction.Category);
    }

    [Fact]
    public void ValidateArguments_AcceptsFloat32ArrayAndInteger()
    {
        PcaFitAggregateFunction fn = new();
        Assert.Equal(DataKind.Struct, fn.ValidateArguments([DataKind.Float32, DataKind.Int32]));
    }

    [Fact]
    public void ValidateArguments_WrongArity_Throws()
    {
        PcaFitAggregateFunction fn = new();
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments([DataKind.Float32]));
    }

    // ───────────────────────── eigenstructure ─────────────────────────

    [Fact]
    public async Task AxisAlignedData_RecoversAxesAndRatios()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 2, AxisAlignedPoints);

        DataValue result = await acc.ResultAsync(frame);
        var (mean, components, ratio, shape) = ReadModel(result, arena, types);

        Assert.Equal([2, 2], shape);
        AssertComponentEquals([0f, 0f], mean);
        AssertComponentEquals([1f, 0f], components.AsSpan(0, 2));
        AssertComponentEquals([0f, 1f], components.AsSpan(2, 2));
        Assert.Equal(0.9f, ratio[0], 3);
        Assert.Equal(0.1f, ratio[1], 3);
    }

    [Fact]
    public async Task ShiftedData_CentersOnMean_SameComponents()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 2,
            AxisAlignedPoints.Select(p => new[] { p[0] + 10f, p[1] + 20f }));

        DataValue result = await acc.ResultAsync(frame);
        var (mean, components, _, _) = ReadModel(result, arena, types);

        AssertComponentEquals([10f, 20f], mean);
        AssertComponentEquals([1f, 0f], components.AsSpan(0, 2));
        AssertComponentEquals([0f, 1f], components.AsSpan(2, 2));
    }

    [Fact]
    public async Task PlaneEmbeddedIn3D_RecoversPlaneAxes()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 2,
            AxisAlignedPoints.Select(p => new[] { p[0], p[1], 0f }));

        DataValue result = await acc.ResultAsync(frame);
        var (mean, components, ratio, shape) = ReadModel(result, arena, types);

        Assert.Equal([2, 3], shape);
        AssertComponentEquals([0f, 0f, 0f], mean);
        AssertComponentEquals([1f, 0f, 0f], components.AsSpan(0, 3));
        AssertComponentEquals([0f, 1f, 0f], components.AsSpan(3, 3));
        Assert.Equal(0.9f, ratio[0], 3);
        Assert.Equal(0.1f, ratio[1], 3);
    }

    [Fact]
    public async Task DiagonalData_SignConvention_LargestComponentPositive()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 1, [
            [1f, 1f],
            [-1f, -1f],
            [2f, 2f],
            [-2f, -2f],
        ]);

        DataValue result = await acc.ResultAsync(frame);
        var (_, components, ratio, shape) = ReadModel(result, arena, types);

        Assert.Equal([1, 2], shape);
        float invSqrt2 = 1f / MathF.Sqrt(2f);
        AssertComponentEquals([invSqrt2, invSqrt2], components);
        Assert.Equal(1.0f, ratio[0], 3);
    }

    [Fact]
    public async Task KLessThanD_ReturnsOnlyTopComponents()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 1, AxisAlignedPoints);

        DataValue result = await acc.ResultAsync(frame);
        var (_, components, ratio, shape) = ReadModel(result, arena, types);

        Assert.Equal([1, 2], shape);
        AssertComponentEquals([1f, 0f], components);
        Assert.Equal(0.9f, ratio[0], 3);
        Assert.Single(ratio);
    }

    // ───────────────────────── null / error handling ─────────────────────────

    [Fact]
    public async Task NullVectors_AreSkipped()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();

        acc.Accumulate([DataValue.NullArrayOf(DataKind.Float32), DataValue.FromInt32(2)], frame);
        AccumulatePoints(acc, arena, frame, k: 2, AxisAlignedPoints);
        acc.Accumulate([DataValue.NullArrayOf(DataKind.Float32), DataValue.FromInt32(2)], frame);

        DataValue result = await acc.ResultAsync(frame);
        var (_, components, _, _) = ReadModel(result, arena, types);
        AssertComponentEquals([1f, 0f], components.AsSpan(0, 2));
    }

    [Fact]
    public async Task EmptyGroup_ReturnsNull()
    {
        var (_, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task SingleVector_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(1)], frame);

        await Assert.ThrowsAsync<FunctionArgumentException>(async () => await acc.ResultAsync(frame));
    }

    [Fact]
    public void DimensionMismatch_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();
        acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(1)], frame);

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([Vec(arena, 1f, 2f, 3f), DataValue.FromInt32(1)], frame));
        Assert.Contains("2", ex.Message);
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void KGreaterThanD_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();

        Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(3)], frame));
    }

    [Fact]
    public void KLessThanOne_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();

        Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(0)], frame));
    }

    // ───────────────────────── lifecycle ─────────────────────────

    [Fact]
    public async Task Merge_MatchesSingleAccumulator()
    {
        float[][] points =
        [
            [1.0f, 2.0f, 0.5f],
            [-1.5f, 0.5f, 1.0f],
            [2.5f, -1.0f, -0.5f],
            [0.5f, 1.5f, 2.0f],
            [-2.0f, -0.5f, 1.5f],
            [1.5f, 0.0f, -1.0f],
            [-0.5f, -2.0f, 0.0f],
            [3.0f, 1.0f, 0.5f],
        ];

        var (arena, types, frame) = CreateFitContext();
        PcaFitAggregateFunction fn = new();

        IAggregateAccumulator whole = fn.CreateAccumulator();
        AccumulatePoints(whole, arena, frame, k: 2, points);
        var (wholeMean, wholeComponents, wholeRatio, _) = ReadModel(await whole.ResultAsync(frame), arena, types);

        IAggregateAccumulator left = fn.CreateAccumulator();
        IAggregateAccumulator right = fn.CreateAccumulator();
        AccumulatePoints(left, arena, frame, k: 2, points.Take(3));
        AccumulatePoints(right, arena, frame, k: 2, points.Skip(3));
        await left.MergeAsync(right, frame);
        var (mergedMean, mergedComponents, mergedRatio, _) = ReadModel(await left.ResultAsync(frame), arena, types);

        AssertComponentEquals(wholeMean, mergedMean, tol: 1e-4f);
        AssertComponentEquals(wholeComponents, mergedComponents, tol: 1e-4f);
        AssertComponentEquals(wholeRatio, mergedRatio, tol: 1e-4f);
    }

    [Fact]
    public async Task Reset_ReusesAccumulatorCleanly()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new PcaFitAggregateFunction().CreateAccumulator();

        // First group: 3-dim data.
        AccumulatePoints(acc, arena, frame, k: 1,
            AxisAlignedPoints.Select(p => new[] { p[0], p[1], 0f }));
        _ = await acc.ResultAsync(frame);

        acc.Reset();

        // Second group: 2-dim data — must not trip the first group's pinned d.
        AccumulatePoints(acc, arena, frame, k: 2, AxisAlignedPoints);
        var (_, components, _, shape) = ReadModel(await acc.ResultAsync(frame), arena, types);
        Assert.Equal([2, 2], shape);
        AssertComponentEquals([1f, 0f], components.AsSpan(0, 2));
    }

    // ───────────────────────── end-to-end SQL ─────────────────────────

    [Fact]
    public async Task Sql_GroupByPipeline_FieldAccessByName()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE docs (v Array<Float32>(2))");
        catalog.Plan("INSERT INTO docs VALUES " +
            "([cast(3.0 as Float32), cast(0.0 as Float32)])," +
            "([cast(-3.0 as Float32), cast(0.0 as Float32)])," +
            "([cast(0.0 as Float32), cast(1.0 as Float32)])," +
            "([cast(0.0 as Float32), cast(-1.0 as Float32)])");

        // Caller-supplied stores must carry a baseline reference (the
        // ExecutionContext only adds one for stores it owns) — without it, the
        // GROUP BY returning its last input batch drops the refcount to zero
        // and pools the arena mid-query.
        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT model['mean'] AS m, model['variance_ratio'] AS r " +
            "FROM (SELECT pca_fit_agg(v, 2) AS model FROM docs) s",
            catalog, store: arena);

        Assert.Single(rows);
        float[] mean = rows[0]["m"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray();
        float[] ratio = rows[0]["r"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray();
        AssertComponentEquals([0f, 0f], mean);
        Assert.Equal(0.9f, ratio[0], 3);
        Assert.Equal(0.1f, ratio[1], 3);
    }

    [Fact]
    public async Task Sql_WindowForm_FitAndProjectInOneQuery()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE docs (id Int32, v Array<Float32>(2))");
        catalog.Plan("INSERT INTO docs VALUES " +
            "(1, [cast(3.0 as Float32), cast(0.0 as Float32)])," +
            "(2, [cast(-3.0 as Float32), cast(0.0 as Float32)])," +
            "(3, [cast(0.0 as Float32), cast(1.0 as Float32)])," +
            "(4, [cast(0.0 as Float32), cast(-1.0 as Float32)])");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id, pca_project(m, v) AS xy " +
            "FROM (SELECT id, v, pca_fit_agg(v, 2) OVER () AS m FROM docs) s ORDER BY id",
            catalog, store: arena);

        Assert.Equal(4, rows.Count);
        // Identity basis, zero mean — projection returns each point unchanged.
        float[][] expected = [[3f, 0f], [-3f, 0f], [0f, 1f], [0f, -1f]];
        for (int i = 0; i < 4; i++)
        {
            float[] xy = rows[i]["xy"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray();
            AssertComponentEquals(expected[i], xy);
        }
    }
}
