using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// End-to-end exercise of <c>COPY (query) TO 'path' (FORMAT parquet)</c> —
/// the parser slot, <see cref="ExportPlan"/> + planner glue,
/// <see cref="Heliosoph.DatumV.Export.Parquet.ParquetExportFormat"/>, and
/// <see cref="Heliosoph.DatumV.Export.Parquet.ParquetExportSink"/>.
///
/// The headline case exports a typed-media (Image) column alongside a scalar
/// (Int32) and verifies the bytes round-trip through Parquet's
/// <c>BYTE_ARRAY</c> physical type — the v1 Inline media disposition.
/// </summary>
public sealed class ParquetExportSinkTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;

    public ParquetExportSinkTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"datum-parquet-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CopyToParquet_ScalarsOnly_WritesReadableFile()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id", "name", "score"],
            columnKinds: [DataKind.Int32, DataKind.String, DataKind.Float64],
            rows:
            [
                [1, "alice", 0.10],
                [2, "bob",   0.55],
                [3, "carol", 0.95],
            ]));

        string outPath = Path.Combine(_scratchDir, "scalars.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name, score FROM public.scalars) TO '{EscapeSql(outPath)}' (FORMAT parquet)");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath), $"export did not produce a file at '{outPath}'");
        Assert.True(new FileInfo(outPath).Length > 0, "export file is empty");

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(3, fields.Length);
        Assert.Equal("id", fields[0].Name);
        Assert.Equal("name", fields[1].Name);
        Assert.Equal("score", fields[2].Name);
    }

    [Fact]
    public async Task CopyToParquet_WithImageColumn_RoundTripsBytes()
    {
        // Two distinct payloads so the round-trip can confirm per-row identity.
        byte[] image1 = MakeFakeImageBytes(0xAA, 32);
        byte[] image2 = MakeFakeImageBytes(0xBB, 96);

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows:
            [
                [1, image1],
                [2, image2],
            ]));

        string outPath = Path.Combine(_scratchDir, "samples.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}' (FORMAT parquet)");

        ExportPlan exportPlan = Assert.IsType<ExportPlan>(plan);
        Assert.Equal("Copy", exportPlan.ExplainTree.OperatorName);

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath), $"export did not produce a file at '{outPath}'");

        // Round-trip: open the file via Parquet.Net directly so the test
        // verifies the on-disk bytes rather than re-using our own writer's
        // schema translation. Image is stored as a BYTE_ARRAY column, which
        // Parquet.Net surfaces as a CLR byte[].
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(2, fields.Length);
        Assert.Equal("id", fields[0].Name);
        Assert.Equal("pic", fields[1].Name);

        Assert.Equal(1, reader.RowGroupCount);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(2, rg.RowCount);

        DataColumn idColumn = await rg.ReadColumnAsync(fields[0]);
        DataColumn imageColumn = await rg.ReadColumnAsync(fields[1]);

        int[] ids = ConvertToNonNullable<int>(idColumn.Data);
        byte[][] images = ConvertToReferenceArray<byte[]>(imageColumn.Data);

        Assert.Equal([1, 2], ids);
        Assert.Equal(image1, images[0]);
        Assert.Equal(image2, images[1]);
    }

    [Fact]
    public async Task CopyToParquet_InfersFormatFromExtension_WhenFormatOptionAbsent()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2]]));

        string outPath = Path.Combine(_scratchDir, "by-extension.parquet");

        // No (FORMAT parquet) option, no option block at all — the planner
        // should still resolve the format from the .parquet extension.
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}'");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task CopyToParquet_RejectsUnknownFormatName()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "x.weird");

        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}' (FORMAT mystery)"));
        Assert.Contains("unknown format 'mystery'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyToParquet_RejectsUnsupportedExtensionWithoutFormatOption()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "x.unknown");

        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}'"));
        Assert.Contains("cannot infer format from extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] MakeFakeImageBytes(byte fillByte, int length)
    {
        // Not a real PNG — the sink writes raw BYTE_ARRAY, so it never decodes.
        // Each fixture is just a recognisable byte pattern that the round-trip
        // assertion can compare against.
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(fillByte ^ i);
        return bytes;
    }

    /// <summary>
    /// Parquet.Net's <see cref="DataColumn.Data"/> for a nullable value-type
    /// column returns <c>T?[]</c>; for a non-nullable column it returns
    /// <c>T[]</c>. The export sink writes Int32 with <see cref="ColumnInfo.Nullable"/>
    /// = <c>true</c> (the planner's default when the source is an
    /// in-memory provider), so the read path lands a <c>int?[]</c>. Collapse
    /// to <c>int[]</c> for assertion convenience — the test fixtures never
    /// emit nulls.
    /// </summary>
    private static T[] ConvertToNonNullable<T>(Array raw) where T : struct
    {
        if (raw is T[] direct) return direct;
        if (raw is T?[] nullable)
        {
            T[] result = new T[nullable.Length];
            for (int i = 0; i < nullable.Length; i++) result[i] = nullable[i] ?? default;
            return result;
        }
        throw new InvalidOperationException(
            $"Unexpected column data shape: {raw.GetType().Name}");
    }

    private static T[] ConvertToReferenceArray<T>(Array raw) where T : class
    {
        if (raw is T[] direct) return direct;
        if (raw is T?[] nullable)
        {
            T[] result = new T[nullable.Length];
            for (int i = 0; i < nullable.Length; i++) result[i] = nullable[i]!;
            return result;
        }
        throw new InvalidOperationException(
            $"Unexpected column data shape: {raw.GetType().Name}");
    }

    private static string EscapeSql(string path) => path.Replace("'", "''");
}
