using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Aggregates;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions.Aggregates;

/// <summary>
/// Tests for <see cref="PlotScatterAggregateFunction"/> — <c>plot_scatter_agg(x, y [, class] [, options])</c>.
/// Verifies pixel placement under the padded autoscale mapping, categorical
/// palette selection, the options struct (size, background, point_size),
/// null handling, and the aggregate lifecycle (Merge equivalence, Reset).
/// </summary>
public sealed class PlotScatterAggregateFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_scatter_{Guid.NewGuid():N}");
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

    private (Arena Arena, TypeRegistry Types, InvocationFrame Frame) CreateContext()
    {
        Arena arena = CreateArena();
        TypeRegistry types = new();
        InvocationFrame frame = InvocationFrame.Symmetric(arena, null, types);
        return (arena, types, frame);
    }

    private static DataValue BuildOptions(
        Arena arena, TypeRegistry types,
        int? width = null, int? height = null,
        (byte R, byte G, byte B) ? background = null,
        float? pointSize = null)
    {
        List<StructFieldDescriptor> descriptors = [];
        List<DataValue> fields = [];
        int int32Type = types.InternScalarType(DataKind.Int32);
        int floatType = types.InternScalarType(DataKind.Float32);
        int colorType = types.InternScalarType(DataKind.Color);
        if (width is { } w)
        {
            descriptors.Add(new StructFieldDescriptor("width", int32Type));
            fields.Add(DataValue.FromInt32(w));
        }
        if (height is { } h)
        {
            descriptors.Add(new StructFieldDescriptor("height", int32Type));
            fields.Add(DataValue.FromInt32(h));
        }
        if (background is { } bg)
        {
            descriptors.Add(new StructFieldDescriptor("background", colorType));
            fields.Add(DataValue.FromColor(bg.R, bg.G, bg.B));
        }
        if (pointSize is { } p)
        {
            descriptors.Add(new StructFieldDescriptor("point_size", floatType));
            fields.Add(DataValue.FromFloat32(p));
        }
        ushort typeId = (ushort)types.InternStructType(descriptors.ToArray());
        return DataValue.FromStruct(fields.ToArray(), arena, typeId);
    }

    private static SKBitmap DecodeResult(DataValue result, Arena arena)
    {
        Assert.Equal(DataKind.Image, result.Kind);
        Assert.False(result.IsNull);
        byte[] bytes = result.AsByteSpan(arena).ToArray();
        SKBitmap? bitmap = SKBitmap.Decode(bytes);
        Assert.NotNull(bitmap);
        return bitmap!;
    }

    private static void AssertPixel(SKBitmap bitmap, int x, int y, byte r, byte g, byte b, int tol = 3)
    {
        SKColor pixel = bitmap.GetPixel(x, y);
        Assert.True(
            System.Math.Abs(pixel.Red - r) <= tol
            && System.Math.Abs(pixel.Green - g) <= tol
            && System.Math.Abs(pixel.Blue - b) <= tol
            && pixel.Alpha == 255,
            $"pixel ({x},{y}) = #{pixel.Red:X2}{pixel.Green:X2}{pixel.Blue:X2} a={pixel.Alpha}, "
            + $"expected #{r:X2}{g:X2}{b:X2}");
    }

    // Tableau-10 first two entries — the documented default palette.
    private const byte P0R = 0x4E, P0G = 0x79, P0B = 0xA7;
    private const byte P1R = 0xF2, P1G = 0x8E, P1B = 0x2B;

    // ───────────────────────── metadata ─────────────────────────

    [Fact]
    public void Metadata_Exposes()
    {
        Assert.Equal("plot_scatter_agg", PlotScatterAggregateFunction.Name);
        Assert.Equal(FunctionCategory.Aggregate, PlotScatterAggregateFunction.Category);
    }

    [Fact]
    public void ValidateArguments_AllArities()
    {
        PlotScatterAggregateFunction fn = new();
        Assert.Equal(DataKind.Image, fn.ValidateArguments([DataKind.Float32, DataKind.Float32]));
        Assert.Equal(DataKind.Image, fn.ValidateArguments([DataKind.Float64, DataKind.Float64, DataKind.Int32]));
        Assert.Equal(DataKind.Image, fn.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Struct]));
        Assert.Equal(DataKind.Image, fn.ValidateArguments([DataKind.Float32, DataKind.Float32, DataKind.Int32, DataKind.Struct]));
        Assert.Throws<ArgumentException>(() => fn.ValidateArguments([DataKind.Float32]));
    }

    // ───────────────────────── rendering ─────────────────────────

    [Fact]
    public async Task TwoPoints_LandAtPaddedScalePositions()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 100, height: 100, pointSize: 4f);

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), options], frame);
        acc.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f), options], frame);

        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        Assert.Equal(100, bitmap.Width);
        Assert.Equal(100, bitmap.Height);

        // Padding 0.05 → domain [-0.05, 1.05]; px = (v + 0.05) / 1.1 * 99.
        // (0,0) → (4.5, 94.5); (1,1) → (94.5, 4.5) with y inverted.
        AssertPixel(bitmap, 4, 94, P0R, P0G, P0B);
        AssertPixel(bitmap, 94, 4, P0R, P0G, P0B);
        // Off-point background stays transparent.
        Assert.Equal(0, bitmap.GetPixel(50, 50).Alpha);
    }

    [Fact]
    public async Task Classes_UseDistinctPaletteColors()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 100, height: 100, pointSize: 4f);

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), DataValue.FromInt32(0), options], frame);
        acc.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f), DataValue.FromInt32(1), options], frame);

        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        AssertPixel(bitmap, 4, 94, P0R, P0G, P0B);
        AssertPixel(bitmap, 94, 4, P1R, P1G, P1B);
    }

    [Fact]
    public async Task Background_FillsCanvas()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 50, height: 40, background: ((byte)10, (byte)20, (byte)30));

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), options], frame);
        acc.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f), options], frame);

        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        Assert.Equal(50, bitmap.Width);
        Assert.Equal(40, bitmap.Height);
        AssertPixel(bitmap, 25, 20, 10, 20, 30);
    }

    [Fact]
    public async Task DefaultOptions_Render1200x630()
    {
        var (arena, _, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f)], frame);
        acc.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f)], frame);

        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(630, bitmap.Height);
    }

    [Fact]
    public async Task SinglePoint_DegenerateRange_CentersPoint()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 100, height: 100, pointSize: 4f);

        acc.Accumulate([DataValue.FromFloat32(7f), DataValue.FromFloat32(7f), options], frame);

        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        // Zero range on both axes expands symmetrically — the point centers.
        AssertPixel(bitmap, 49, 49, P0R, P0G, P0B, tol: 8);
    }

    // ───────────────────────── null / error handling ─────────────────────────

    [Fact]
    public async Task NullCoordinates_SkipPoint()
    {
        var (arena, types, frame) = CreateContext();
        PlotScatterAggregateFunction fn = new();

        IAggregateAccumulator withNulls = fn.CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 100, height: 100);
        withNulls.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), options], frame);
        withNulls.Accumulate([DataValue.Null(DataKind.Float32), DataValue.FromFloat32(5f), options], frame);
        withNulls.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f), options], frame);

        IAggregateAccumulator clean = fn.CreateAccumulator();
        clean.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), options], frame);
        clean.Accumulate([DataValue.FromFloat32(1f), DataValue.FromFloat32(1f), options], frame);

        byte[] withNullsBytes = (await withNulls.ResultAsync(frame)).AsByteSpan(arena).ToArray();
        byte[] cleanBytes = (await clean.ResultAsync(frame)).AsByteSpan(arena).ToArray();
        Assert.Equal(cleanBytes, withNullsBytes);
    }

    [Fact]
    public async Task EmptyGroup_ReturnsNull()
    {
        var (_, _, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();

        DataValue result = await acc.ResultAsync(frame);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void InvalidDimensions_Throw()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue options = BuildOptions(arena, types, width: 0, height: 100);

        Assert.Throws<FunctionArgumentException>(
            () => acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), options], frame));
    }

    // ───────────────────────── lifecycle ─────────────────────────

    [Fact]
    public async Task Merge_MatchesSingleAccumulator()
    {
        var (arena, types, frame) = CreateContext();
        PlotScatterAggregateFunction fn = new();
        DataValue options = BuildOptions(arena, types, width: 120, height: 90, pointSize: 3f);

        float[][] points = [[0f, 0f], [1f, 2f], [2f, 1f], [3f, 3f]];

        IAggregateAccumulator whole = fn.CreateAccumulator();
        foreach (float[] p in points)
        {
            whole.Accumulate([DataValue.FromFloat32(p[0]), DataValue.FromFloat32(p[1]), options], frame);
        }

        IAggregateAccumulator left = fn.CreateAccumulator();
        IAggregateAccumulator right = fn.CreateAccumulator();
        foreach (float[] p in points.Take(2))
        {
            left.Accumulate([DataValue.FromFloat32(p[0]), DataValue.FromFloat32(p[1]), options], frame);
        }
        foreach (float[] p in points.Skip(2))
        {
            right.Accumulate([DataValue.FromFloat32(p[0]), DataValue.FromFloat32(p[1]), options], frame);
        }
        await left.MergeAsync(right, frame);

        byte[] wholeBytes = (await whole.ResultAsync(frame)).AsByteSpan(arena).ToArray();
        byte[] mergedBytes = (await left.ResultAsync(frame)).AsByteSpan(arena).ToArray();
        Assert.Equal(wholeBytes, mergedBytes);
    }

    [Fact]
    public async Task Reset_ReusesAccumulatorCleanly()
    {
        var (arena, types, frame) = CreateContext();
        IAggregateAccumulator acc = new PlotScatterAggregateFunction().CreateAccumulator();
        DataValue big = BuildOptions(arena, types, width: 200, height: 200);
        DataValue small = BuildOptions(arena, types, width: 50, height: 50);

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), big], frame);
        _ = await acc.ResultAsync(frame);

        acc.Reset();

        acc.Accumulate([DataValue.FromFloat32(0f), DataValue.FromFloat32(0f), small], frame);
        using SKBitmap bitmap = DecodeResult(await acc.ResultAsync(frame), arena);
        Assert.Equal(50, bitmap.Width);
        Assert.Equal(50, bitmap.Height);
    }

    // ───────────────────────── end-to-end SQL ─────────────────────────

    [Fact]
    public async Task Sql_FullEmbeddingsPipeline_PcaKmeansScatter()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE corpus (id Int32, emb Array<Float32>(3))");
        // Two separable groups in 3-D.
        catalog.Plan("INSERT INTO corpus VALUES " +
            "(1, [cast(0.0 as Float32), cast(0.1 as Float32), cast(0.0 as Float32)])," +
            "(2, [cast(0.2 as Float32), cast(0.0 as Float32), cast(0.1 as Float32)])," +
            "(3, [cast(0.1 as Float32), cast(0.2 as Float32), cast(0.0 as Float32)])," +
            "(4, [cast(9.0 as Float32), cast(9.1 as Float32), cast(9.0 as Float32)])," +
            "(5, [cast(9.2 as Float32), cast(9.0 as Float32), cast(9.1 as Float32)])," +
            "(6, [cast(9.1 as Float32), cast(9.2 as Float32), cast(9.0 as Float32)])");

        Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT plot_scatter_agg(xy[1], xy[2], cluster, {width: 320, height: 200}) AS hero " +
            "FROM (SELECT pca_project(pca, emb) AS xy, " +
            "             nearest_centroid(km['centroids'], emb) AS cluster " +
            "      FROM (SELECT emb, pca_fit_agg(emb, 2) OVER () AS pca, " +
            "                        kmeans_fit_agg(emb, 2) OVER () AS km FROM corpus) f) p",
            catalog, store: arena);

        Assert.Single(rows);
        using SKBitmap bitmap = DecodeResult(rows[0]["hero"], arena);
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(200, bitmap.Height);

        // Two clusters → at least two distinct opaque colors on the canvas.
        HashSet<uint> opaque = [];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                if (c.Alpha == 255) opaque.Add((uint)c);
            }
        }
        Assert.True(opaque.Count >= 2, $"expected ≥2 distinct point colors, found {opaque.Count}");
    }
}
