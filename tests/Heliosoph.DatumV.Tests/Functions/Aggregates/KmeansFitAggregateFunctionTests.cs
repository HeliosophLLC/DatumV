using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="KmeansFitAggregateFunction"/> — <c>kmeans_fit_agg(vec, k [, options])</c>.
/// Verifies the model-struct contract (centroids / inertia / iterations with a
/// registry-resolvable TypeId), recovery of well-separated clusters, the options
/// struct, determinism under a fixed seed, null / dimension / arity error
/// handling, and the aggregate lifecycle (Merge equivalence, Reset reuse).
/// </summary>
public sealed class KmeansFitAggregateFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_kmeans_{Guid.NewGuid():N}");
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
    /// Two tight clusters: four points around (0, 0) and four around (10, 10).
    /// Cluster means are exactly (0, 0) and (10, 10).
    /// </summary>
    private static readonly float[][] TwoClusterPoints =
    [
        [-1f, 0f], [1f, 0f], [0f, -1f], [0f, 1f],
        [9f, 10f], [11f, 10f], [10f, 9f], [10f, 11f],
    ];

    private static DataValue Vec(Arena arena, params float[] values) =>
        DataValue.FromArenaArray<float>(values, DataKind.Float32, arena);

    private (Arena Arena, TypeRegistry Types, InvocationFrame Frame) CreateFitContext()
    {
        Arena arena = CreateArena();
        TypeRegistry types = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena, null, types);
        return (arena, types, frame);
    }

    private static void AccumulatePoints(
        IAggregateAccumulator acc, Arena arena, in InvocationFrame frame, int k, IEnumerable<float[]> points)
    {
        foreach (float[] p in points)
        {
            acc.Accumulate([Vec(arena, p), DataValue.FromInt32(k)], frame);
        }
    }

    private DataValue BuildOptions(Arena arena, TypeRegistry types, int? seed = null, int? maxIter = null, float? tol = null)
    {
        List<StructFieldDescriptor> descriptors = [];
        List<DataValue> fields = [];
        int int32Type = types.InternScalarType(DataKind.Int32);
        int floatType = types.InternScalarType(DataKind.Float32);
        if (seed is { } s)
        {
            descriptors.Add(new StructFieldDescriptor("seed", int32Type));
            fields.Add(DataValue.FromInt32(s));
        }
        if (maxIter is { } m)
        {
            descriptors.Add(new StructFieldDescriptor("max_iter", int32Type));
            fields.Add(DataValue.FromInt32(m));
        }
        if (tol is { } t)
        {
            descriptors.Add(new StructFieldDescriptor("tol", floatType));
            fields.Add(DataValue.FromFloat32(t));
        }
        ushort typeId = (ushort)types.InternStructType(descriptors.ToArray());
        return DataValue.FromStruct(fields.ToArray(), arena, typeId);
    }

    private static (float[] Centroids, float Inertia, int Iterations, int[] Shape) ReadModel(
        DataValue result, Arena arena, TypeRegistry types)
    {
        Assert.Equal(DataKind.Struct, result.Kind);
        Assert.False(result.IsNull);
        Assert.NotEqual(0, (int)result.TypeId);

        TypeDescriptor? desc = types.GetDescriptor(result.TypeId);
        Assert.NotNull(desc?.Fields);
        int centroidsIdx = desc!.FindFieldIndex("centroids");
        int inertiaIdx = desc.FindFieldIndex("inertia");
        int iterationsIdx = desc.FindFieldIndex("iterations");
        Assert.True(centroidsIdx >= 0, "model struct must carry a 'centroids' field");
        Assert.True(inertiaIdx >= 0, "model struct must carry an 'inertia' field");
        Assert.True(iterationsIdx >= 0, "model struct must carry an 'iterations' field");

        DataValue[] fields = result.AsStruct(arena);
        Assert.True(fields[centroidsIdx].IsMultiDim, "centroids must be a multi-dim [k, d] array");
        return (
            fields[centroidsIdx].AsArraySpan<float>(arena).ToArray(),
            fields[inertiaIdx].AsFloat32(),
            fields[iterationsIdx].AsInt32(),
            fields[centroidsIdx].GetShape(arena).ToArray());
    }

    /// <summary>
    /// Asserts the k×d centroid matrix contains each expected centroid exactly
    /// once (order-agnostic — k-means centroid order depends on seeding).
    /// </summary>
    private static void AssertCentroidSet(float[][] expected, float[] centroidsFlat, int d, float tol = 1e-3f)
    {
        Assert.Equal(expected.Length * d, centroidsFlat.Length);
        bool[] matched = new bool[expected.Length];
        for (int c = 0; c < expected.Length; c++)
        {
            ReadOnlySpan<float> row = centroidsFlat.AsSpan(c * d, d);
            bool found = false;
            for (int e = 0; e < expected.Length && !found; e++)
            {
                if (matched[e]) continue;
                bool close = true;
                for (int i = 0; i < d && close; i++)
                {
                    close = System.Math.Abs(expected[e][i] - row[i]) <= tol;
                }
                if (close)
                {
                    matched[e] = true;
                    found = true;
                }
            }
            Assert.True(found, $"centroid row {c} [{string.Join(", ", row.ToArray())}] matches no expected centroid");
        }
    }

    // ───────────────────────── metadata ─────────────────────────

    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("kmeans_fit_agg", KmeansFitAggregateFunction.Name);
        Assert.Equal(FunctionCategory.Aggregate, KmeansFitAggregateFunction.Category);
    }

    [Fact]
    public void ValidateArguments_TwoAndThreeArgForms()
    {
        KmeansFitAggregateFunction fn = new();
        Assert.Equal(DataKind.Struct, fn.ValidateArguments([DataKind.Float32, DataKind.Int32]));
        Assert.Equal(DataKind.Struct, fn.ValidateArguments([DataKind.Float32, DataKind.Int32, DataKind.Struct]));
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments([DataKind.Float32]));
    }

    // ───────────────────────── clustering ─────────────────────────

    [Fact]
    public async Task TwoWellSeparatedClusters_RecoversMeans()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 2, TwoClusterPoints);

        DataValue result = await acc.ResultAsync(frame);
        var (centroids, inertia, iterations, shape) = ReadModel(result, arena, types);

        Assert.Equal([2, 2], shape);
        AssertCentroidSet([[0f, 0f], [10f, 10f]], centroids, d: 2);
        // Each cluster: 4 points at squared distance 1 from its mean.
        Assert.Equal(8f, inertia, 2);
        Assert.True(iterations >= 1);
    }

    [Fact]
    public async Task KEqualsOne_CentroidIsMean()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 1, [[1f, 2f], [3f, 4f], [5f, 6f]]);

        DataValue result = await acc.ResultAsync(frame);
        var (centroids, _, _, shape) = ReadModel(result, arena, types);

        Assert.Equal([1, 2], shape);
        Assert.Equal(3f, centroids[0], 3);
        Assert.Equal(4f, centroids[1], 3);
    }

    [Fact]
    public async Task SameSeed_IsDeterministic()
    {
        var (arena, types, frame) = CreateFitContext();
        KmeansFitAggregateFunction fn = new();

        float[] first = null!;
        for (int run = 0; run < 2; run++)
        {
            IAggregateAccumulator acc = fn.CreateAccumulator();
            AccumulatePoints(acc, arena, frame, k: 2, TwoClusterPoints);
            var (centroids, _, _, _) = ReadModel(await acc.ResultAsync(frame), arena, types);
            if (run == 0) first = centroids;
            else Assert.Equal(first, centroids);
        }
    }

    [Fact]
    public async Task Options_SeedAndMaxIterRespected()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, seed: 7, maxIter: 1);

        foreach (float[] p in TwoClusterPoints)
        {
            acc.Accumulate([Vec(arena, p), DataValue.FromInt32(2), options], frame);
        }

        DataValue result = await acc.ResultAsync(frame);
        var (_, _, iterations, shape) = ReadModel(result, arena, types);
        Assert.Equal([2, 2], shape);
        Assert.Equal(1, iterations);
    }

    // ───────────────────────── null / error handling ─────────────────────────

    [Fact]
    public async Task NullVectors_AreSkipped()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();

        acc.Accumulate([DataValue.NullArrayOf(DataKind.Float32), DataValue.FromInt32(2)], frame);
        AccumulatePoints(acc, arena, frame, k: 2, TwoClusterPoints);

        DataValue result = await acc.ResultAsync(frame);
        var (centroids, _, _, _) = ReadModel(result, arena, types);
        AssertCentroidSet([[0f, 0f], [10f, 10f]], centroids, d: 2);
    }

    [Fact]
    public async Task EmptyGroup_ReturnsNull()
    {
        var (_, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task FewerVectorsThanK_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();
        AccumulatePoints(acc, arena, frame, k: 3, [[1f, 2f], [3f, 4f]]);

        await Assert.ThrowsAsync<FunctionArgumentException>(async () => await acc.ResultAsync(frame));
    }

    [Fact]
    public void DimensionMismatch_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();
        acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(1)], frame);

        Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([Vec(arena, 1f, 2f, 3f), DataValue.FromInt32(1)], frame));
    }

    [Fact]
    public void KLessThanOne_Throws()
    {
        var (arena, _, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();

        Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([Vec(arena, 1f, 2f), DataValue.FromInt32(0)], frame));
    }

    // ───────────────────────── lifecycle ─────────────────────────

    [Fact]
    public async Task Merge_MatchesSingleAccumulator()
    {
        var (arena, types, frame) = CreateFitContext();
        KmeansFitAggregateFunction fn = new();

        IAggregateAccumulator whole = fn.CreateAccumulator();
        AccumulatePoints(whole, arena, frame, k: 2, TwoClusterPoints);
        var (wholeCentroids, wholeInertia, _, _) = ReadModel(await whole.ResultAsync(frame), arena, types);

        IAggregateAccumulator left = fn.CreateAccumulator();
        IAggregateAccumulator right = fn.CreateAccumulator();
        AccumulatePoints(left, arena, frame, k: 2, TwoClusterPoints.Take(4));
        AccumulatePoints(right, arena, frame, k: 2, TwoClusterPoints.Skip(4));
        await left.MergeAsync(right, frame);
        var (mergedCentroids, mergedInertia, _, _) = ReadModel(await left.ResultAsync(frame), arena, types);

        Assert.Equal(wholeCentroids, mergedCentroids);
        Assert.Equal(wholeInertia, mergedInertia, 3);
    }

    [Fact]
    public async Task Reset_ReusesAccumulatorCleanly()
    {
        var (arena, types, frame) = CreateFitContext();
        IAggregateAccumulator acc = new KmeansFitAggregateFunction().CreateAccumulator();

        AccumulatePoints(acc, arena, frame, k: 1, [[1f, 2f, 3f], [4f, 5f, 6f]]);
        _ = await acc.ResultAsync(frame);

        acc.Reset();

        AccumulatePoints(acc, arena, frame, k: 2, TwoClusterPoints);
        var (centroids, _, _, shape) = ReadModel(await acc.ResultAsync(frame), arena, types);
        Assert.Equal([2, 2], shape);
        AssertCentroidSet([[0f, 0f], [10f, 10f]], centroids, d: 2);
    }

    // ───────────────────────── end-to-end SQL ─────────────────────────

    [Fact]
    public async Task Sql_ClusterAndLabelRows()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE pts (id Int32, v Array<Float32>(2))");
        catalog.Plan("INSERT INTO pts VALUES " +
            "(1, [cast(-1.0 as Float32), cast(0.0 as Float32)])," +
            "(2, [cast(1.0 as Float32), cast(0.0 as Float32)])," +
            "(3, [cast(9.0 as Float32), cast(10.0 as Float32)])," +
            "(4, [cast(11.0 as Float32), cast(10.0 as Float32)])");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT id, nearest_centroid(m['centroids'], v) AS cluster " +
            "FROM (SELECT id, v, kmeans_fit_agg(v, 2, {seed: 7}) OVER () AS m FROM pts) s ORDER BY id",
            catalog, store: arena);

        Assert.Equal(4, rows.Count);
        int c1 = rows[0]["cluster"].AsInt32();
        int c2 = rows[1]["cluster"].AsInt32();
        int c3 = rows[2]["cluster"].AsInt32();
        int c4 = rows[3]["cluster"].AsInt32();

        // 1-based ids; points 1+2 share a cluster, 3+4 share the other.
        Assert.InRange(c1, 1, 2);
        Assert.InRange(c3, 1, 2);
        Assert.Equal(c1, c2);
        Assert.Equal(c3, c4);
        Assert.NotEqual(c1, c3);
    }
}
