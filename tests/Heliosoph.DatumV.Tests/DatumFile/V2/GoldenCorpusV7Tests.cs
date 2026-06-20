using System.Runtime.CompilerServices;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.DatumFile.V2;

/// <summary>
/// Reader-regression corpus for the v7 on-disk format. The
/// <c>GoldenV7/</c> sibling directory holds committed <c>.datum</c> /
/// <c>.datum-blob</c> bytes produced by today's writer; the tests in
/// this class open each one and validate the deserializer surfaces the
/// expected schema + spot-check values. A future refactor that
/// accidentally breaks v7 parsing fails one of these tests.
/// </summary>
/// <remarks>
/// <para>
/// To refresh the committed bytes (e.g. when the writer legitimately
/// changes output for a new feature), set the
/// <c>DATUMV_REGEN_GOLDEN</c> environment variable to <c>1</c> and
/// run <see cref="Regenerate_AllGoldenFiles"/>. Without that flag the
/// regenerator is a no-op that reports success — chosen over the
/// natural <c>[Fact(Skip = ...)]</c> shape because some versions of
/// the VS Code C# Dev Kit adapter mis-render skipped facts as
/// failures. Example regen invocation:
/// </para>
/// <code>
/// DATUMV_REGEN_GOLDEN=1 dotnet test \
///   --filter FullyQualifiedName~GoldenCorpusV7Tests.Regenerate
/// </code>
/// <para>
/// Tests resolve the golden directory via <see cref="CallerFilePathAttribute"/>
/// rather than CopyToOutputDirectory entries so the same file serves
/// every IDE / CI host without csproj plumbing — the source tree always
/// contains both the test code and the committed bytes side by side.
/// </para>
/// </remarks>
public sealed class GoldenCorpusV7Tests : ServiceTestBase
{
    private const string KitchenSinkFile = "kitchen_sink.datum";
    private const string MultiChapterFile = "multi_chapter.datum";
    private const string StructAndComputedFile = "struct_and_computed.datum";
    private const string SidecarHeavyFile = "sidecar_heavy.datum";

    private static string GoldenDir([CallerFilePath] string callerPath = "")
        => Path.Combine(Path.GetDirectoryName(callerPath)!, "GoldenV7");

    private static string GoldenPath(string fileName) =>
        Path.Combine(GoldenDir(), fileName);

    [Fact]
    public void Regenerate_AllGoldenFiles()
    {
        // No-op unless the developer explicitly opted in. Reports as
        // passed in Test Explorer so a normal run leaves the corpus
        // alone without showing a red/yellow marker. See class-level
        // remarks for the trade and the regen invocation.
        if (Environment.GetEnvironmentVariable("DATUMV_REGEN_GOLDEN") != "1") return;

        string dir = GoldenDir();
        Directory.CreateDirectory(dir);

        BuildKitchenSink(Path.Combine(dir, KitchenSinkFile));
        BuildMultiChapter(Path.Combine(dir, MultiChapterFile));
        BuildStructAndComputed(Path.Combine(dir, StructAndComputedFile));
        BuildSidecarHeavy(Path.Combine(dir, SidecarHeavyFile));
    }

    // ───────────────────────── Validation tests ─────────────────────────

    [Fact]
    public void KitchenSink_OpensAndReadsCorrectly()
    {
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(GoldenPath(KitchenSinkFile));

        Assert.Equal(DatumFormatV2.FormatVersion, reader.Header.MinReaderVersion);
        Assert.Equal(200L, reader.Header.TotalRowCount);

        IReadOnlyList<ColumnFooterV2> cols = reader.Footer.Columns;
        Assert.Equal(6, cols.Count);
        Assert.Equal("id", cols[0].Descriptor.Name);
        Assert.Equal(DataKind.Int32, cols[0].Descriptor.Kind);
        Assert.False(cols[0].Descriptor.IsNullable);
        Assert.Equal("flag", cols[1].Descriptor.Name);
        Assert.Equal(EncoderKind.BitPackedBoolean, cols[1].Descriptor.Encoder);
        Assert.True(cols[1].Descriptor.IsNullable);
        Assert.Equal("score", cols[2].Descriptor.Name);
        Assert.Equal(DataKind.Float64, cols[2].Descriptor.Kind);
        Assert.Equal("tag", cols[3].Descriptor.Name);
        Assert.Equal(DataKind.String, cols[3].Descriptor.Kind);
        Assert.Equal(8, cols[3].Descriptor.MaxLength);
        Assert.False(cols[3].Descriptor.IsBlankPadded);
        Assert.Equal("tag_padded", cols[4].Descriptor.Name);
        Assert.Equal(8, cols[4].Descriptor.MaxLength);
        Assert.True(cols[4].Descriptor.IsBlankPadded);
        Assert.Equal("weights", cols[5].Descriptor.Name);
        Assert.Equal(DataKind.Float32, cols[5].Descriptor.Kind);
        Assert.True(cols[5].Descriptor.IsArray);
        int[]? weightsShape = cols[5].Descriptor.FixedShape;
        Assert.NotNull(weightsShape);
        Assert.Equal([3], weightsShape);

        FooterPrologueV4 prolog = reader.Footer.Prologue;
        Assert.Equal(0, prolog.IdentityColumnIndex);
        Assert.Equal(1L, prolog.IdentitySeed);
        Assert.Equal(1L, prolog.IdentityStep);
        Assert.Single(prolog.PrimaryKeyColumnIndices);
        Assert.Equal((ushort)0, prolog.PrimaryKeyColumnIndices[0]);
        Assert.Single(prolog.ColumnDefaults);
        Assert.Equal((ushort)2, prolog.ColumnDefaults[0].ColumnIndex);

        // Spot-check row 0 of the Int32 IDENTITY column — should be the seed.
        Arena arena = CreateArena();
        IPageDecoderV2 idDecoder = reader.OpenPageDecoder(columnIndex: 0, pageIndex: 0, eagerStore: arena);
        DataValue idRow0 = idDecoder.ReadValue(0);
        Assert.Equal(1, idRow0.AsInt32());
    }

    [Fact]
    public void MultiChapter_OpensAndReadsCorrectly()
    {
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(GoldenPath(MultiChapterFile));

        Assert.Equal(DatumFormatV2.FormatVersion, reader.Header.MinReaderVersion);
        // Designed to span > one chapter at the small page size used by
        // the generator (page=256, chapter=16384, file=20000 rows).
        Assert.Equal(20_000L, reader.Header.TotalRowCount);
        Assert.Equal(256, reader.Header.PageSize);

        IReadOnlyList<ColumnFooterV2> cols = reader.Footer.Columns;
        Assert.Equal(2, cols.Count);
        Assert.Equal("seq", cols[0].Descriptor.Name);
        Assert.Equal(DataKind.Int64, cols[0].Descriptor.Kind);
        Assert.Equal("bucket", cols[1].Descriptor.Name);
        Assert.Equal(DataKind.Int8, cols[1].Descriptor.Kind);

        // Multi-chapter assertion — there should be more than one chapter
        // zone map per column, proving the chapter aggregator ran.
        Assert.True(cols[0].ChapterZoneMaps.Count > 1,
            $"expected >1 chapter zone map, got {cols[0].ChapterZoneMaps.Count}");

        // Spot-check value at the chapter boundary (row 16384, the first
        // row of chapter index 1) — the seq column carries the row index.
        Arena arena = CreateArena();
        int rowsPerPage = reader.Header.PageSize;
        int boundaryPageIndex = 16_384 / rowsPerPage;
        IPageDecoderV2 seqDecoder = reader.OpenPageDecoder(
            columnIndex: 0, pageIndex: boundaryPageIndex, eagerStore: arena);
        DataValue boundary = seqDecoder.ReadValue(16_384 % rowsPerPage);
        Assert.Equal(16_384L, boundary.AsInt64());
    }

    [Fact]
    public void StructAndComputed_OpensAndReadsCorrectly()
    {
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(GoldenPath(StructAndComputedFile));

        Assert.Equal(DatumFormatV2.FormatVersion, reader.Header.MinReaderVersion);
        Assert.Equal(50L, reader.Header.TotalRowCount);

        // v5 type-table flag and footer block.
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasTypeTable) != 0,
            "struct column must light HasTypeTable");
        Assert.Single(reader.Footer.TypeTable);
        Assert.Equal((ushort)1, reader.Footer.TypeTable[0].OnDiskTypeId);

        // v6 computed-columns flag and footer block.
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasColumnComputeds) != 0,
            "GENERATED ALWAYS column must light HasColumnComputeds");
        Assert.Single(reader.Footer.ColumnComputeds);
        Assert.Equal((ushort)2, reader.Footer.ColumnComputeds[0].ColumnIndex);
        Assert.Equal("42", reader.Footer.ColumnComputeds[0].SqlFragment);

        IReadOnlyList<ColumnFooterV2> cols = reader.Footer.Columns;
        Assert.Equal(3, cols.Count);
        Assert.Equal(DataKind.Int32, cols[0].Descriptor.Kind);
        Assert.Equal(DataKind.Struct, cols[1].Descriptor.Kind);
        Assert.NotNull(cols[1].StructTypeId);
        Assert.Equal((ushort)1, cols[1].StructTypeId!.Value);
        Assert.Equal(DataKind.Float64, cols[2].Descriptor.Kind);
    }

    [Fact]
    public void SidecarHeavy_OpensAndReadsCorrectly()
    {
        string datumPath = GoldenPath(SidecarHeavyFile);
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);

        Assert.Equal(DatumFormatV2.FormatVersion, reader.Header.MinReaderVersion);
        Assert.Equal(20L, reader.Header.TotalRowCount);
        Assert.True((reader.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) != 0,
            "long-payload file must light HasSidecarReferences");

        // The companion .datum-blob must exist alongside.
        string sidecarPath = Path.ChangeExtension(datumPath, ".datum-blob");
        Assert.True(File.Exists(sidecarPath),
            $"expected sidecar at {sidecarPath}");

        IReadOnlyList<ColumnFooterV2> cols = reader.Footer.Columns;
        Assert.Equal(2, cols.Count);
        Assert.Equal("id", cols[0].Descriptor.Name);
        Assert.Equal(DataKind.Int32, cols[0].Descriptor.Kind);
        Assert.Equal("payload", cols[1].Descriptor.Name);
        Assert.Equal(DataKind.String, cols[1].Descriptor.Kind);
        Assert.Equal(EncoderKind.VariableSlot, cols[1].Descriptor.Encoder);
    }

    // ───────────────────────── Generators ─────────────────────────

    /// <summary>
    /// Builds the kitchen-sink corpus file: six columns exercising every
    /// encoder, the v6 prologue blocks (IDENTITY / PK / ColumnDefaults),
    /// and the column flags HasMaxLength, IsBlankPadded, HasFixedShape.
    /// 200 rows, single chapter, no sidecar (all string values fit inline).
    /// </summary>
    private void BuildKitchenSink(string path)
    {
        const int rowCount = 200;

        ColumnDescriptorV2 colId = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colFlag = new("flag", DataKind.Boolean, EncoderKind.BitPackedBoolean, IsNullable: true);
        ColumnDescriptorV2 colScore = new("score", DataKind.Float64, EncoderKind.FixedWidth, IsNullable: true);
        ColumnDescriptorV2 colTag = new("tag", DataKind.String, EncoderKind.VariableSlot, IsNullable: false, MaxLength: 8);
        ColumnDescriptorV2 colTagPadded = new("tag_padded", DataKind.String, EncoderKind.VariableSlot, IsNullable: false, MaxLength: 8, IsBlankPadded: true);
        ColumnDescriptorV2 colWeights = new("weights", DataKind.Float32, EncoderKind.VariableSlot, IsNullable: false, IsArray: true, FixedShape: [3]);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["id", "flag", "score", "tag", "tag_padded", "weights"]);
        Arena arena = CreateArena();

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize(
            [colId, colFlag, colScore, colTag, colTagPadded, colWeights],
            columnDefaults: [new ColumnDefaultV4(ColumnIndex: 2, SqlFragment: "0.0")],
            identity: new IdentityWriterSpec(ColumnIndex: 0, Seed: 1, Step: 1),
            primaryKeyColumnIndices: [(ushort)0]);

        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(6);
            row[0] = DataValue.FromInt32(i + 1);
            row[1] = (i % 7 == 0) ? DataValue.Null(DataKind.Boolean) : DataValue.FromBoolean(i % 2 == 0);
            row[2] = DataValue.FromFloat64(i * 0.5);
            row[3] = DataValue.FromString($"t{i:D4}", arena);
            row[4] = DataValue.FromString($"p{i:D4}", arena);
            // 3 floats × 4 bytes = 12 bytes, well inside the 16-byte inline
            // tier so the kitchen-sink file stays sidecar-free.
            float[] w = [i * 1f, i * 2f, i * 3f];
            row[5] = DataValue.FromInlineArray<float>(w, DataKind.Float32);
            batch.Add(row);
        }
        writer.WriteRowBatch(batch);
        writer.UpdateIdentityNextValue(rowCount + 1);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a file large enough to span a chapter boundary, so the
    /// chapter zone-map aggregator runs. Uses a small page size (256) to
    /// keep the file modest while still landing in chapter-tier territory
    /// (chapter = PagesPerChapter × pageSize = 64 × 256 = 16384 rows).
    /// Exercises the uint16 PageSize encoding from the v7 header layout.
    /// </summary>
    private void BuildMultiChapter(string path)
    {
        const int rowCount = 20_000;
        const int pageSize = 256;

        ColumnDescriptorV2 colSeq = new("seq", DataKind.Int64, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colBucket = new("bucket", DataKind.Int8, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["seq", "bucket"]);
        Arena arena = CreateArena();

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.SetPageSize(pageSize);
        writer.Initialize([colSeq, colBucket]);

        // Stream in chunks to keep the in-memory batch bounded.
        const int chunk = 1024;
        for (int start = 0; start < rowCount; start += chunk)
        {
            int n = Math.Min(chunk, rowCount - start);
            RowBatch batch = pool.RentRowBatch(lookup, capacity: n, arena: arena);
            for (int i = 0; i < n; i++)
            {
                long row = start + i;
                DataValue[] vals = pool.RentDataValues(2);
                vals[0] = DataValue.FromInt64(row);
                vals[1] = DataValue.FromInt8((sbyte)(row % 127));
                batch.Add(vals);
            }
            writer.WriteRowBatch(batch);
        }
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a file with one Struct column (exercises the v5 type table
    /// + per-column StructTypeId) and one GENERATED ALWAYS column
    /// (exercises the v6 ColumnComputeds block). 50 rows, single chapter,
    /// sidecar present (struct descriptor blob lives there).
    /// </summary>
    private void BuildStructAndComputed(string path)
    {
        const int rowCount = 50;

        ColumnDescriptorV2 colId = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colPoint = new("point", DataKind.Struct, EncoderKind.VariableSlot, IsNullable: false);
        ColumnDescriptorV2 colComputed = new("answer", DataKind.Float64, EncoderKind.FixedWidth, IsNullable: false);

        TypeRegistry writerRegistry = new();
        int structTypeId = writerRegistry.InternStructType(
        [
            new StructFieldDescriptor("x", writerRegistry.InternScalarType(DataKind.Float32)),
            new StructFieldDescriptor("y", writerRegistry.InternScalarType(DataKind.Float32)),
        ]);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["id", "point", "answer"]);
        Arena arena = CreateArena();

        string sidecarPath = Path.ChangeExtension(path, ".datum-blob");
        using DatumFileWriterV2 writer = new(path, sidecarPath);
        writer.SetTypeRegistry(writerRegistry);
        writer.Initialize(
            [colId, colPoint, colComputed],
            columnDefaults: null,
            identity: null,
            primaryKeyColumnIndices: null,
            columnComputeds: [new ColumnComputedV4(ColumnIndex: 2, SqlFragment: "42")]);

        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(3);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromStruct(
                [DataValue.FromFloat32(i * 1.5f), DataValue.FromFloat32(i * 2.5f)],
                arena, (ushort)structTypeId);
            row[2] = DataValue.FromFloat64(42.0);
            batch.Add(row);
        }
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a file where the variable-slot column's values are all
    /// long enough to spill to the sidecar (the inline budget is 16
    /// bytes). HasSidecarReferences fires, and the .datum-blob is
    /// non-empty. Catches the pointer-slot + sidecar mmap path.
    /// </summary>
    private void BuildSidecarHeavy(string path)
    {
        const int rowCount = 20;

        ColumnDescriptorV2 colId = new("id", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colPayload = new("payload", DataKind.String, EncoderKind.VariableSlot, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["id", "payload"]);
        Arena arena = CreateArena();

        string sidecarPath = Path.ChangeExtension(path, ".datum-blob");
        using DatumFileWriterV2 writer = new(path, sidecarPath);
        writer.Initialize([colId, colPayload]);

        RowBatch batch = pool.RentRowBatch(lookup, capacity: rowCount, arena: arena);
        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(i);
            // 48-byte payload — well past the 16-byte inline tier so each
            // row's value lands as a sidecar pointer.
            row[1] = DataValue.FromString(
                $"sidecar-bound-payload-row-{i:D3}-padding-to-spill-bytes",
                arena);
            batch.Add(row);
        }
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }
}
