namespace DatumIngest.Tests.Manifest;

using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Statistics;

public sealed class ManifestBuilderTests : ServiceTestBase
{
    private readonly Arena _arena;

    public ManifestBuilderTests()
    {
        _arena = CreateArena();
    }

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }
    [Fact]
    public void Build_NumericColumn_ProducesNumericFeatureManifest()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(3.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        Assert.Equal(3, manifest.RowCount);
        Assert.Single(manifest.Features);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal("value", feature.Name);
        Assert.Equal(DataKind.Float32, feature.Kind);
        Assert.Equal(3, feature.Count);
        Assert.Equal(0, feature.NullCount);
        Assert.Equal(3, feature.ValidCount);
        Assert.Equal(1.0, feature.Min);
        Assert.Equal(3.0, feature.Max);
        Assert.Equal(2.0, feature.Mean, 1e-10);
        Assert.NotEmpty(feature.Histogram.BinEdges);
    }

    [Fact]
    public void Build_StringColumn_ProducesStringFeatureManifest()
    {
        ColumnLookup columnLookup = new (["name"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("Alice", _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("Bob", _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("Charlotte", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.Equal("name", feature.Name);
        Assert.Equal(DataKind.String, feature.Kind);
        Assert.Equal(3, feature.Count);
        Assert.Equal(3, feature.MinLength);  // "Bob"
        Assert.Equal(9, feature.MaxLength);  // "Charlotte"
        Assert.Equal(3, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_VectorColumn_ProducesArrayFeatureManifest()
    {
        ColumnLookup columnLookup = new (["embedding"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<float>([1.0f, 2.0f, 3.0f], DataKind.Float32, _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<float>([4.0f, 5.0f], DataKind.Float32, _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> kinds = new()
        {
            ["embedding"] = new ColumnInfo("embedding", DataKind.Float32, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        ArrayFeatureManifest feature = Assert.IsType<ArrayFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2, feature.MinLength);
        Assert.Equal(3, feature.MaxLength);
        Assert.Equal(5, feature.ElementStats.Count);
        Assert.Equal(1.0, feature.ElementStats.Min);
        Assert.Equal(5.0, feature.ElementStats.Max);

        // ||[1,2,3]||₂ = sqrt(14), ||[4,5]||₂ = sqrt(41)
        Assert.Equal(Math.Sqrt(14.0), feature.NormMin, 1e-10);
        Assert.Equal(Math.Sqrt(41.0), feature.NormMax, 1e-10);
        Assert.Equal((Math.Sqrt(14.0) + Math.Sqrt(41.0)) / 2.0, feature.NormMean, 1e-10);
    }

    [Fact]
    public void Build_Int32ArrayColumn_ProducesArrayFeatureManifestWithElementStats()
    {
        ColumnLookup columnLookup = new(["counts"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<int>([1, 2, 3], DataKind.Int32, _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<int>([4, 5], DataKind.Int32, _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> columns = new()
        {
            ["counts"] = new ColumnInfo("counts", DataKind.Int32, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, columns, 2);

        ArrayFeatureManifest feature = Assert.IsType<ArrayFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Int32, feature.Kind);
        Assert.True(feature.IsArray);
        Assert.Equal(2, feature.MinLength);
        Assert.Equal(3, feature.MaxLength);
        Assert.Equal(5, feature.ElementStats.Count);
        Assert.Equal(1.0, feature.ElementStats.Min);
        Assert.Equal(5.0, feature.ElementStats.Max);
        Assert.Equal(3.0, feature.ElementStats.Mean, 1e-10);
        Assert.Equal(Math.Sqrt(14.0), feature.NormMin, 1e-10);
        Assert.Equal(Math.Sqrt(41.0), feature.NormMax, 1e-10);
    }

    [Fact]
    public void Build_Float64ArrayColumn_ProducesArrayFeatureManifest()
    {
        ColumnLookup columnLookup = new(["weights"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<double>([1.5, 2.5, 3.5], DataKind.Float64, _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<double>([0.0, 0.0], DataKind.Float64, _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> columns = new()
        {
            ["weights"] = new ColumnInfo("weights", DataKind.Float64, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, columns, 2);

        ArrayFeatureManifest feature = Assert.IsType<ArrayFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Float64, feature.Kind);
        Assert.Equal(5, feature.ElementStats.Count);
        Assert.Equal(0.0, feature.ElementStats.Min);
        Assert.Equal(3.5, feature.ElementStats.Max);
        Assert.Equal(2, feature.ZeroElementCount);
        // Second array is all-zero, so ZeroArrayCount should pick it up.
        Assert.Equal(1, feature.ZeroArrayCount);
        Assert.Equal(0.0, feature.NormMin);   // [0,0] has zero norm
    }

    [Fact]
    public void Build_Float16ArrayColumn_ProducesArrayFeatureManifest()
    {
        ColumnLookup columnLookup = new(["embedding16"]);
        StatisticsCollector collector = new();
        Half[] values1 = [(Half)1.0f, (Half)2.0f, (Half)3.0f];
        Half[] values2 = [(Half)4.0f, (Half)5.0f];
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<Half>(values1, DataKind.Float16, _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<Half>(values2, DataKind.Float16, _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> columns = new()
        {
            ["embedding16"] = new ColumnInfo("embedding16", DataKind.Float16, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, columns, 2);

        ArrayFeatureManifest feature = Assert.IsType<ArrayFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Float16, feature.Kind);
        Assert.Equal(5, feature.ElementStats.Count);
        Assert.Equal(1.0, feature.ElementStats.Min, 1e-3);
        Assert.Equal(5.0, feature.ElementStats.Max, 1e-3);
        Assert.Equal(3.0, feature.ElementStats.Mean, 1e-3);
    }

    [Fact]
    public void Build_UInt8ArrayColumn_StaysOnBinaryPath_NotArrayPath()
    {
        // UInt8+IsArray is the byte-blob path — must produce BinaryFeatureManifest,
        // not ArrayFeatureManifest. Guard against the typed-array dispatch
        // accidentally swallowing byte arrays after PR14f's broader gate.
        ColumnLookup columnLookup = new(["blob"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromByteArray([1, 2, 3, 4], _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromByteArray([5, 6], _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> columns = new()
        {
            ["blob"] = new ColumnInfo("blob", DataKind.UInt8, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, columns, 2);

        Assert.IsType<BinaryFeatureManifest>(manifest.Features[0]);
        // No array_stats result should have been produced for this column.
        Assert.False(stats["blob"].Results.ContainsKey("array_stats"));
    }

    [Fact]
    public void Build_BinaryColumn_ProducesBinaryFeatureManifest()
    {
        ColumnLookup columnLookup = new (["raw"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromByteArray([1, 2, 3, 4, 5], _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromByteArray([10, 20], _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, ColumnInfo> columns = new()
        {
            ["raw"] = new ColumnInfo("raw", DataKind.UInt8, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, columns, 2);

        BinaryFeatureManifest feature = Assert.IsType<BinaryFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2.0, feature.SizeStats.Min);
        Assert.Equal(5.0, feature.SizeStats.Max);
    }

    [Fact]
    public void Build_DateColumn_ProducesTemporalFeatureManifest()
    {
        ColumnLookup columnLookup = new (["created"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDate(new DateOnly(2024, 1, 15))), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDate(new DateOnly(2025, 6, 30))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["created"] = DataKind.Date };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        TemporalFeatureManifest feature = Assert.IsType<TemporalFeatureManifest>(manifest.Features[0]);
        Assert.Equal("2024-01-15", feature.Earliest);
        Assert.Equal("2025-06-30", feature.Latest);
    }

    [Fact]
    public void Build_Float16Column_ProducesNumericFeatureManifest()
    {
        ColumnLookup columnLookup = new (["weight"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat16((Half)1.5f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat16((Half)2.5f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat16((Half)3.5f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["weight"] = DataKind.Float16 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Float16, feature.Kind);
        Assert.Equal(1.5, feature.Min, 1e-3);
        Assert.Equal(3.5, feature.Max, 1e-3);
        Assert.Equal(2.5, feature.Mean, 1e-3);
    }

    [Fact]
    public void Build_Int128Column_ProducesNumericFeatureManifest()
    {
        ColumnLookup columnLookup = new (["big"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromInt128((Int128)10)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromInt128((Int128)20)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromInt128((Int128)30)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["big"] = DataKind.Int128 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Int128, feature.Kind);
        Assert.Equal(10.0, feature.Min);
        Assert.Equal(30.0, feature.Max);
    }

    [Fact]
    public void Build_UInt128Column_ProducesNumericFeatureManifest()
    {
        ColumnLookup columnLookup = new (["big"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromUInt128((UInt128)100)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromUInt128((UInt128)200)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["big"] = DataKind.UInt128 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.UInt128, feature.Kind);
        Assert.Equal(100.0, feature.Min);
        Assert.Equal(200.0, feature.Max);
    }

    [Fact]
    public void Build_TimeColumn_ProducesTemporalFeatureManifest()
    {
        ColumnLookup columnLookup = new (["clock"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromTime(new TimeOnly(8, 30, 0))), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromTime(new TimeOnly(17, 45, 12))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["clock"] = DataKind.Time };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        TemporalFeatureManifest feature = Assert.IsType<TemporalFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Time, feature.Kind);
        Assert.StartsWith("08:30:00", feature.Earliest);
        Assert.StartsWith("17:45:12", feature.Latest);
    }

    [Fact]
    public void Build_DecimalColumn_ProducesDecimalFeatureManifest()
    {
        ColumnLookup columnLookup = new (["amount"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(1.50m)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(2.25m)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(3.00m)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["amount"] = DataKind.Decimal };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        DecimalFeatureManifest feature = Assert.IsType<DecimalFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Decimal, feature.Kind);
        Assert.Equal(1.50m, feature.Min);
        Assert.Equal(3.00m, feature.Max);
        Assert.Equal(2.25m, feature.Mean);
        Assert.False(feature.IntegerValued);
    }

    [Fact]
    public void Build_DecimalColumn_PreservesFullPrecisionPast2Pow53()
    {
        // The whole point of Decimal is precision past double's 2^53 mantissa.
        // 18014398509481985 = 2^54 + 1 — would round to 2^54 if widened to double.
        ColumnLookup columnLookup = new (["big"]);
        StatisticsCollector collector = new();
        decimal preciseValue = 18014398509481985m;
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(preciseValue)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["big"] = DataKind.Decimal };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        DecimalFeatureManifest feature = Assert.IsType<DecimalFeatureManifest>(manifest.Features[0]);
        // Bitwise-exact preservation — the failure mode this test pins is
        // "decimal accidentally routed through NumericAccumulator's double path".
        Assert.Equal(preciseValue, feature.Min);
        Assert.Equal(preciseValue, feature.Max);
        Assert.True(feature.IntegerValued);
    }

    [Fact]
    public void Build_DecimalColumn_AllIntegers_FlagsIntegerValued()
    {
        ColumnLookup columnLookup = new (["qty"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(1m)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(2m)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDecimal(3m)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["qty"] = DataKind.Decimal };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        DecimalFeatureManifest feature = Assert.IsType<DecimalFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IntegerValued);
    }

    [Fact]
    public void Build_UuidColumn_ProducesUuidFeatureManifest_VersionCounts()
    {
        ColumnLookup columnLookup = new (["id"]);
        StatisticsCollector collector = new();

        // Two v4 (random) and one v7 (timestamp) UUID. v4 has its version
        // nibble = 4; v7 = 7.
        Guid v4a = Guid.Parse("00000000-0000-4000-8000-000000000001");
        Guid v4b = Guid.Parse("00000000-0000-4000-8000-000000000002");
        Guid v7  = Guid.Parse("01900000-0000-7000-8000-000000000003");

        collector.AddRow(MakeRow(columnLookup, DataValue.FromUuid(v4a)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromUuid(v4b)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromUuid(v7)),  _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Uuid };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        UuidFeatureManifest feature = Assert.IsType<UuidFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2L, feature.VersionCounts[4]);
        Assert.Equal(1L, feature.VersionCounts[7]);
        Assert.NotNull(feature.EmbeddedTimestampEarliest);
        Assert.NotNull(feature.EmbeddedTimestampLatest);
    }

    [Fact]
    public void Build_JsonColumn_ProducesJsonFeatureManifest_RootTypesAndTopLevelFields()
    {
        ColumnLookup columnLookup = new (["doc"]);
        StatisticsCollector collector = new();

        // Three values: two object-rooted with overlapping keys, one
        // scalar-rooted (number). Top-level field counts should reflect
        // the two objects.
        DataValue obj1 = MakeJsonValue("""{"id":1,"name":"alice"}""");
        DataValue obj2 = MakeJsonValue("""{"id":2,"tags":["a","b"]}""");
        DataValue scalar = MakeJsonValue("""42""");

        collector.AddRow(MakeRow(columnLookup, obj1), _arena);
        collector.AddRow(MakeRow(columnLookup, obj2), _arena);
        collector.AddRow(MakeRow(columnLookup, scalar), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["doc"] = DataKind.Json };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        JsonFeatureManifest feature = Assert.IsType<JsonFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2L, feature.RootTypeCounts["object"]);
        Assert.Equal(1L, feature.RootTypeCounts["number"]);
        Assert.Equal(2L, feature.TopLevelFieldCounts["id"]);     // both objects
        Assert.Equal(1L, feature.TopLevelFieldCounts["name"]);   // only obj1
        Assert.Equal(1L, feature.TopLevelFieldCounts["tags"]);   // only obj2
        Assert.True(feature.MaxDepth >= 2);                      // {tags: [...]} → depth ≥ 3
    }

    private DataValue MakeJsonValue(string json)
    {
        // Encode JSON text to canonical CBOR (the wire form for DataKind.Json),
        // then store via DataValue.FromJson.
        byte[] cbor = DatumIngest.Functions.Json.CborJsonCodec.EncodeFromJsonText(json);
        return DataValue.FromJson(cbor, _arena);
    }

    [Fact]
    public void Build_DurationColumn_ProducesNumericFeatureManifestInSeconds()
    {
        ColumnLookup columnLookup = new (["latency"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDuration(TimeSpan.FromSeconds(1))), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDuration(TimeSpan.FromSeconds(5))), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDuration(TimeSpan.FromSeconds(9))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["latency"] = DataKind.Duration };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(DataKind.Duration, feature.Kind);
        Assert.Equal(1.0, feature.Min);
        Assert.Equal(9.0, feature.Max);
        Assert.Equal(5.0, feature.Mean, 1e-10);
    }

    [Fact]
    public void Build_MultipleColumns_ProducesCorrectTypes()
    {
        ColumnLookup columnLookup = new (["id", "name", "score"]);
        StatisticsCollector collector = new();

        Row row = MakeRow(
            columnLookup,
            DataValue.FromFloat32(1.0f),
            DataValue.FromString("test", _arena),
            DataValue.FromUInt8(200));

        collector.AddRow(row, _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["id"] = DataKind.Float32,
            ["name"] = DataKind.String,
            ["score"] = DataKind.UInt8
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        Assert.Equal(3, manifest.Features.Count);
        Assert.Contains(manifest.Features, f => f is NumericFeatureManifest && f.Name == "id");
        Assert.Contains(manifest.Features, f => f is StringFeatureManifest && f.Name == "name");
        Assert.Contains(manifest.Features, f => f is NumericFeatureManifest && f.Name == "score");
    }

    [Fact]
    public void Build_NullValues_TrackedInNullCount()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(1, feature.Count);
        Assert.Equal(1, feature.NullCount);
        Assert.Equal(1, feature.ValidCount);
    }

    [Fact]
    public void Build_TopKValues_Populated()
    {
        ColumnLookup columnLookup = new (["category"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("A", _arena)), _arena);
        }

        for (int i = 0; i < 5; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("B", _arena)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["category"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 15);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotEmpty(feature.TopKValues);
        Assert.Equal("A", feature.TopKValues[0].Value);
        Assert.Equal(10, feature.TopKValues[0].Frequency);
    }

    [Fact]
    public void Build_GeneratedAtUtc_IsSet()
    {
        ColumnLookup columnLookup = new (["x"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);

        Assert.True(manifest.GeneratedAtUtc > DateTime.MinValue);
        Assert.True(manifest.GeneratedAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void Build_NullRatio_ComputedCorrectly()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(3.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 4);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0.5, feature.NullRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_NullRatio_ZeroRows_IsNull()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        // No columns in stats when no rows added — build with empty stats but rowCount 0
        // Add at least one row so the column exists, then build with rowCount = 0
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        stats = collector.GetStatistics();

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Null(feature.NullRatio);
    }

    [Fact]
    public void Build_NullRatio_NoNulls_IsZero()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0.0, feature.NullRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_MissingRuns_TrackedCorrectly()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        // Run 1: [null, null]
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        // non-null
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        // Run 2: [null]
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        // non-null
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 5);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(2, feature.MissingRuns);
    }

    [Fact]
    public void Build_MissingRuns_NoNulls_IsZero()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Equal(0, feature.MissingRuns);
    }

    [Fact]
    public void Build_ConstantColumn_IsConstantTrue()
    {
        ColumnLookup columnLookup = new (["price"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(0.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(0.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(0.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsConstant);
        Assert.Equal(1, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_VaryingColumn_IsConstantFalse()
    {
        ColumnLookup columnLookup = new (["price"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(3.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsConstant);
    }

    [Fact]
    public void Build_AllNullColumn_IsConstantTrue()
    {
        ColumnLookup columnLookup = new (["price"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["price"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsConstant);
        Assert.Equal(0, feature.EstimatedDistinctCount);
    }

    [Fact]
    public void Build_DominantValueRatio_NearConstant()
    {
        ColumnLookup columnLookup = new (["status"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 99; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("active", _arena)), _arena);
        }

        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("inactive", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["status"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotNull(feature.DominantValueRatio);
        Assert.Equal(0.99, feature.DominantValueRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_DominantValueRatio_UniformDistribution()
    {
        ColumnLookup columnLookup = new (["cat"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 20; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("A", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("B", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("C", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("D", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("E", _arena)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.NotNull(feature.DominantValueRatio);
        Assert.Equal(0.2, feature.DominantValueRatio!.Value, 1e-10);
    }

    [Fact]
    public void Build_DominantValueRatio_ZeroRows_IsNull()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Null(feature.DominantValueRatio);
    }

    [Fact]
    public void Build_NearConstantColumn_IsNearConstantTrue()
    {
        ColumnLookup columnLookup = new (["status"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 99; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("active", _arena)), _arena);
        }

        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("inactive", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["status"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IsNearConstant);
    }

    [Fact]
    public void Build_UniformColumn_IsNearConstantFalse()
    {
        ColumnLookup columnLookup = new (["cat"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 20; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("A", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("B", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("C", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("D", _arena)), _arena);
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("E", _arena)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    [Fact]
    public void Build_ZeroRows_IsNearConstantFalse()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 0);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    [Fact]
    public void Build_BoundaryRatio_IsNearConstantFalse()
    {
        ColumnLookup columnLookup = new (["flag"]);
        StatisticsCollector collector = new();

        // 98 of one value + 2 of another in 100 rows = exactly 0.98 ratio
        for (int i = 0; i < 98; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("yes", _arena)), _arena);
        }

        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("no", _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("no", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        StringFeatureManifest feature = Assert.IsType<StringFeatureManifest>(manifest.Features[0]);
        Assert.False(feature.IsNearConstant);
    }

    private static byte[] MakeMinimalJpeg(int width, int height, int channels)
    {
        byte[] data = new byte[20];
        data[0] = 0xFF;
        data[1] = 0xD8;
        data[2] = 0xFF;
        data[3] = 0xC0;
        data[4] = 0x00;
        data[5] = 0x0B;
        data[6] = 0x08;
        data[7] = (byte)(height >> 8);
        data[8] = (byte)(height & 0xFF);
        data[9] = (byte)(width >> 8);
        data[10] = (byte)(width & 0xFF);
        data[11] = (byte)channels;
        return data;
    }

    // ─────────────── CharacterClass ───────────────

    [Fact]
    public void ClassifyCharacterClass_EmptyTopK_ReturnsMixed()
    {
        CharacterClass result = ManifestBuilder.ClassifyCharacterClass([]);

        Assert.Equal(CharacterClass.Mixed, result);
    }

    [Fact]
    public void ClassifyCharacterClass_HexValues_ReturnsHexadecimal()
    {
        List<FrequencyEntry> topK =
        [
            new("06b8999e2fba1a1fbc88172c00ba8bc7", 1),
            new("18955e83d337fd6b2def6b18a428ac77", 1),
            new("AABB0011ccDD2233eeff4455", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Hexadecimal, result);
    }

    [Fact]
    public void ClassifyCharacterClass_Base64Values_ReturnsBase64()
    {
        List<FrequencyEntry> topK =
        [
            new("dGhpcyBpcyBhIHRlc3Q=", 1),
            new("SGVsbG8gV29ybGQ/Kw==", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Base64, result);
    }

    [Fact]
    public void ClassifyCharacterClass_AlphanumericValues_ReturnsAlphanumeric()
    {
        List<FrequencyEntry> topK =
        [
            new("SKU12345XYZ", 1),
            new("PROD9876ABC", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Alphanumeric, result);
    }

    [Fact]
    public void ClassifyCharacterClass_MixedValues_ReturnsMixed()
    {
        List<FrequencyEntry> topK =
        [
            new("Hello, world!", 1),
            new("São Paulo", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Mixed, result);
    }

    [Fact]
    public void ClassifyCharacterClass_HexWithSpaces_ReturnsMixed()
    {
        List<FrequencyEntry> topK =
        [
            new("06b8999e 2fba1a1f", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Mixed, result);
    }

    [Fact]
    public void ClassifyCharacterClass_PureDigits_ReturnsHexadecimal()
    {
        List<FrequencyEntry> topK =
        [
            new("12345678", 1),
            new("99887766", 1),
        ];

        CharacterClass result = ManifestBuilder.ClassifyCharacterClass(topK);

        Assert.Equal(CharacterClass.Hexadecimal, result);
    }
}
