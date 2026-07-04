using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.DatumFile.V2;

/// <summary>
/// End-to-end persistence tests for the v5 struct type table. Each test
/// drives the lower-level writer + reader primitives directly so the
/// assertions can pin file-format invariants (the flag bit, the entries,
/// the per-column StructTypeId) without going through the catalog. The
/// last test — cross-query portability — is the load-bearing one: it
/// proves the runtime↔on-disk translation actually carries field names
/// across two independent <see cref="TypeRegistry"/> instances.
/// </summary>
public sealed class TypeTablePersistenceTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v5_typetable_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void Header_AndFooter_DeclareV5_AndTypeTableFlag()
    {
        // A file that emits a struct column lights HasTypeTable in the
        // header and the footer's TypeTable carries one entry per
        // emitted shape. Files without struct columns (any of the
        // existing v4 round-trip tests) keep HasTypeTable clear.
        string datumPath = Path.Combine(_tempDir, "struct_flag.datum");
        WriteFlatStructFile(datumPath);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.True(
            (reader.Header.Flags & DatumFileFlagsV2.HasTypeTable) != 0,
            "writer must set HasTypeTable when struct columns are present");
        Assert.NotEmpty(reader.Footer.TypeTable);
        Assert.Equal((ushort)1, reader.Footer.TypeTable[0].OnDiskTypeId);
    }

    [Fact]
    public void RoundTrip_FlatStructColumn_FieldNamesSurvive()
    {
        // Struct{x: Int32, y: String} must round-trip with field names —
        // the column footer carries StructTypeId, the sidecar holds the
        // descriptor blob, and DecodeStructEagerly stamps the runtime id
        // so the resulting DataValue's TypeId resolves in the reader's
        // TypeRegistry.
        string datumPath = Path.Combine(_tempDir, "flat_struct.datum");
        WriteFlatStructFile(datumPath);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        TypeRegistry registry = new();
        ushort runtimeStructId = LoadTypeTableAndResolveColumn(reader, datumPath, columnIndex: 0, registry);

        using SidecarReadStore sidecar = SidecarReadStore.OpenWithoutFingerprintCheck(SidecarPath(datumPath));
        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 0, pageIndex: 0,
            sidecarStoreId: 0, sidecarSource: sidecar,
            eagerStore: CreateArena(),
            columnRuntimeStructTypeId: runtimeStructId);

        DataValue first = decoder.ReadValue(0);
        Assert.Equal(DataKind.Struct, first.Kind);
        Assert.Equal(runtimeStructId, first.TypeId);

        TypeDescriptor descriptor = registry.GetDescriptor(runtimeStructId)!;
        Assert.NotNull(descriptor.Fields);
        Assert.Equal("x", descriptor.Fields![0].Name);
        Assert.Equal("y", descriptor.Fields[1].Name);
    }

    [Fact]
    public void RoundTrip_ArrayOfStructColumn_PerElementTypeIdsTranslate()
    {
        // The bigger surface — Array<Struct> with per-element TypeIds in
        // slot bytes. Writer's encoder stamps on-disk ids; reader's
        // AsStructArray translates back through TypeIdTranslationTable.
        string datumPath = Path.Combine(_tempDir, "array_struct.datum");
        WriteArrayOfStructFile(datumPath);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        TypeRegistry registry = new();
        TypeIdTranslationTable translations = new();
        Dictionary<ushort, ushort> onDiskToRuntime = LoadTypeTable(reader, datumPath, registry);
        translations.Register(storeId: 0, onDiskToRuntime);

        using SidecarReadStore sidecar = SidecarReadStore.OpenWithoutFingerprintCheck(SidecarPath(datumPath));
        SidecarRegistry sidecarRegistry = new();
        sidecarRegistry.Register(sidecar);

        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 0, pageIndex: 0,
            sidecarStoreId: 0, sidecarSource: sidecar,
            eagerStore: CreateArena());

        Arena readArena = CreateArena();
        DataValue arrayValue = decoder.ReadValue(0);
        Assert.True(arrayValue.IsArray);
        Assert.Equal(DataKind.Struct, arrayValue.Kind);

        DataValue[] elements = arrayValue.AsStructArray(readArena, sidecarRegistry, translations);
        Assert.Equal(2, elements.Length);
        Assert.NotEqual((ushort)0, elements[0].TypeId);

        // The element's TypeId resolves to a known shape in the reader's
        // registry — field names survive the round trip.
        TypeDescriptor descriptor = registry.GetDescriptor(elements[0].TypeId)!;
        Assert.Equal("label", descriptor.Fields![0].Name);
        Assert.Equal("score", descriptor.Fields[1].Name);
    }

    [Fact]
    public void CrossQueryPortability_FreshRegistryStillSeesFieldNames()
    {
        // The translation layer's whole reason for existing. Write the
        // file from one query/registry, open it with a totally fresh
        // registry, and verify field names still resolve. If the writer
        // had embedded runtime TypeIds verbatim into slot bytes, this
        // would fail — runtime ids are query-local.
        string datumPath = Path.Combine(_tempDir, "cross_query.datum");
        WriteArrayOfStructFile(datumPath);

        // Pretend the read side belongs to a totally different query. The
        // critical property: we never see the write-side registry, just
        // the file's bytes.
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        TypeRegistry freshRegistry = new();
        TypeIdTranslationTable translations = new();
        Dictionary<ushort, ushort> onDiskToRuntime = LoadTypeTable(reader, datumPath, freshRegistry);
        translations.Register(storeId: 0, onDiskToRuntime);

        // Confirm a non-trivial type table — a same-shape file would
        // hide bugs that only matter when ids actually need translating.
        Assert.NotEmpty(onDiskToRuntime);

        using SidecarReadStore sidecar = SidecarReadStore.OpenWithoutFingerprintCheck(SidecarPath(datumPath));
        SidecarRegistry sidecarRegistry = new();
        sidecarRegistry.Register(sidecar);

        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 0, pageIndex: 0,
            sidecarStoreId: 0, sidecarSource: sidecar,
            eagerStore: CreateArena());

        Arena readArena = CreateArena();
        DataValue arrayValue = decoder.ReadValue(0);
        DataValue[] elements = arrayValue.AsStructArray(readArena, sidecarRegistry, translations);

        ushort runtimeId = elements[0].TypeId;
        TypeDescriptor descriptor = freshRegistry.GetDescriptor(runtimeId)!;
        Assert.Equal("label", descriptor.Fields![0].Name);

        // Walk the fields and confirm they all resolve via the fresh
        // registry (a stale or write-side id would throw at GetDescriptor).
        foreach (StructFieldDescriptor field in descriptor.Fields)
        {
            Assert.NotNull(freshRegistry.GetDescriptor(field.TypeId));
        }
    }

    [Fact]
    public void Concurrency_TwoRegistriesPickIndependentRuntimeIds()
    {
        // Pin the race fix. Before PR3-followup, the provider cached a
        // per-column runtime TypeId in a single field; concurrent queries
        // with distinct registries would overwrite each other and the
        // late writer's ids would leak into the early reader's batches.
        // Now that translation happens via the per-call typeIdTranslations
        // argument, two registries can independently resolve the same
        // file and never see each other's runtime ids.
        string datumPath = Path.Combine(_tempDir, "concurrency.datum");
        WriteFlatStructFile(datumPath);

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);

        // Pre-seed registry A so its first interned id is guaranteed
        // distinct from registry B's. Without the pre-seed both registries
        // intern the file's shape into id=1, which would mask a translator
        // that returned the on-disk id verbatim.
        TypeRegistry registryA = new();
        registryA.InternScalarType(DataKind.Boolean);
        registryA.InternScalarType(DataKind.UInt8);

        TypeRegistry registryB = new();

        Dictionary<ushort, ushort> mapA = LoadTypeTable(reader, datumPath, registryA);
        Dictionary<ushort, ushort> mapB = LoadTypeTable(reader, datumPath, registryB);

        ushort onDiskColumnId = reader.Footer.Columns[0].StructTypeId!.Value;
        ushort runtimeIdA = mapA[onDiskColumnId];
        ushort runtimeIdB = mapB[onDiskColumnId];

        Assert.NotEqual(runtimeIdA, runtimeIdB);
        Assert.NotNull(registryA.GetDescriptor(runtimeIdA));
        Assert.NotNull(registryB.GetDescriptor(runtimeIdB));
        // Cross-registry resolution must fail loud — runtime ids are not
        // portable across registries even when the underlying shape matches.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => registryB.GetDescriptor(runtimeIdA));
    }

    [Fact]
    public void OpenForAppend_NoRegistry_PreservesTypeTableAndColumnStructTypeId()
    {
        // The SQL append path (INSERT / CTAS) opens writers without a
        // TypeRegistry. Finalizing such a session over a file that already
        // carries a TypeTable must not drop it — the existing entries,
        // the HasTypeTable flag, and each column's StructTypeId all carry
        // forward so pre-append struct rows stay readable.
        string datumPath = Path.Combine(_tempDir, "append_carry.datum");
        WriteIntPlusStructFile(datumPath);

        string sidecarPath = SidecarPath(datumPath);
        using (DatumFileWriterV2 appender = DatumFileWriterV2.OpenForAppend(datumPath, sidecarPath))
        {
            Pool pool = CreatePool();
            ColumnLookup lookup = new(["n", "s"]);
            Arena arena = CreateArena();
            RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(3);
            row[1] = DataValue.Null(DataKind.Struct);
            batch.Add(row);
            appender.WriteRowBatch(batch);
            appender.FinalizeWriter();
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(3, reader.TotalRowCount);
        Assert.True(
            (reader.Header.Flags & DatumFileFlagsV2.HasTypeTable) != 0,
            "append without a registry must not drop HasTypeTable");
        Assert.NotEmpty(reader.Footer.TypeTable);
        Assert.NotNull(reader.Footer.Columns[1].StructTypeId);

        // Pre-append rows still resolve field names through a fresh registry.
        TypeRegistry registry = new();
        ushort runtimeStructId = LoadTypeTableAndResolveColumn(reader, datumPath, columnIndex: 1, registry);
        TypeDescriptor descriptor = registry.GetDescriptor(runtimeStructId)!;
        Assert.Equal("x", descriptor.Fields![0].Name);
        Assert.Equal("y", descriptor.Fields[1].Name);

        using SidecarReadStore sidecar = SidecarReadStore.OpenWithoutFingerprintCheck(sidecarPath);
        IPageDecoderV2 decoder = reader.OpenPageDecoder(
            columnIndex: 1, pageIndex: 0,
            sidecarStoreId: 0, sidecarSource: sidecar,
            eagerStore: CreateArena(),
            columnRuntimeStructTypeId: runtimeStructId);
        DataValue first = decoder.ReadValue(0);
        Assert.Equal(DataKind.Struct, first.Kind);
        Assert.Equal(runtimeStructId, first.TypeId);
    }

    /// <summary>
    /// Builds a single-column Struct table, writes it through the writer
    /// pipeline. Layout: Struct{x: Int32, y: String}. Returns the runtime
    /// TypeId the writer assigned (via the TypeRegistry the test owns)
    /// for cross-checking.
    /// </summary>
    private void WriteFlatStructFile(string datumPath)
    {
        ColumnDescriptorV2 column = new(
            Name: "s",
            Kind: DataKind.Struct,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: false);

        TypeRegistry writerRegistry = new();
        int structTypeId = writerRegistry.InternStructType(
        [
            new StructFieldDescriptor("x", writerRegistry.InternScalarType(DataKind.Int32)),
            new StructFieldDescriptor("y", writerRegistry.InternScalarType(DataKind.String)),
        ]);

        Pool pool = CreatePool();
        ColumnLookup lookup = new([column.Name]);
        Arena arena = CreateArena();

        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[] r0 = pool.RentDataValues(1);
        r0[0] = DataValue.FromStruct(
            [DataValue.FromInt32(1), DataValue.FromString("alpha", arena)],
            arena, (ushort)structTypeId);
        batch.Add(r0);
        DataValue[] r1 = pool.RentDataValues(1);
        r1[0] = DataValue.FromStruct(
            [DataValue.FromInt32(2), DataValue.FromString("beta", arena)],
            arena, (ushort)structTypeId);
        batch.Add(r1);

        string sidecarPath = SidecarPath(datumPath);
        using DatumFileWriterV2 writer = new(datumPath, sidecarPath);
        writer.SetTypeRegistry(writerRegistry);
        writer.Initialize([column]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a two-column table (n Int32, s nullable Struct{x: Int32, y: String})
    /// with two rows. The struct column is nullable so append tests can add
    /// rows without constructing struct values (mirroring a registry-less
    /// append session).
    /// </summary>
    private void WriteIntPlusStructFile(string datumPath)
    {
        ColumnDescriptorV2 intColumn = new(
            Name: "n",
            Kind: DataKind.Int32,
            Encoder: EncoderKind.FixedWidth,
            IsNullable: false);
        ColumnDescriptorV2 structColumn = new(
            Name: "s",
            Kind: DataKind.Struct,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: true);

        TypeRegistry writerRegistry = new();
        int structTypeId = writerRegistry.InternStructType(
        [
            new StructFieldDescriptor("x", writerRegistry.InternScalarType(DataKind.Int32)),
            new StructFieldDescriptor("y", writerRegistry.InternScalarType(DataKind.String)),
        ]);

        Pool pool = CreatePool();
        ColumnLookup lookup = new([intColumn.Name, structColumn.Name]);
        Arena arena = CreateArena();

        RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[] r0 = pool.RentDataValues(2);
        r0[0] = DataValue.FromInt32(1);
        r0[1] = DataValue.FromStruct(
            [DataValue.FromInt32(10), DataValue.FromString("alpha", arena)],
            arena, (ushort)structTypeId);
        batch.Add(r0);
        DataValue[] r1 = pool.RentDataValues(2);
        r1[0] = DataValue.FromInt32(2);
        r1[1] = DataValue.FromStruct(
            [DataValue.FromInt32(20), DataValue.FromString("beta", arena)],
            arena, (ushort)structTypeId);
        batch.Add(r1);

        string sidecarPath = SidecarPath(datumPath);
        using DatumFileWriterV2 writer = new(datumPath, sidecarPath);
        writer.SetTypeRegistry(writerRegistry);
        writer.Initialize([intColumn, structColumn]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Builds a single-column <c>Array&lt;Struct&gt;</c> table — the YOLO /
    /// SCRFD shape — and writes it. Each row carries a 2-element array of
    /// <c>Struct{label: String, score: Float32}</c>.
    /// </summary>
    private void WriteArrayOfStructFile(string datumPath)
    {
        ColumnDescriptorV2 column = new(
            Name: "detections",
            Kind: DataKind.Struct,
            Encoder: EncoderKind.VariableSlot,
            IsNullable: false,
            IsArray: true);

        TypeRegistry writerRegistry = new();
        int elementTypeId = writerRegistry.InternStructType(
        [
            new StructFieldDescriptor("label", writerRegistry.InternScalarType(DataKind.String)),
            new StructFieldDescriptor("score", writerRegistry.InternScalarType(DataKind.Float32)),
        ]);

        Pool pool = CreatePool();
        ColumnLookup lookup = new([column.Name]);
        Arena arena = CreateArena();

        RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue[] e0 = [DataValue.FromString("cat", arena), DataValue.FromFloat32(0.94f)];
        DataValue[] e1 = [DataValue.FromString("dog", arena), DataValue.FromFloat32(0.71f)];
        DataValue arrayValue = DataValue.FromStructArray([e0, e1], arena, (ushort)elementTypeId);

        DataValue[] row = pool.RentDataValues(1);
        row[0] = arrayValue;
        batch.Add(row);

        string sidecarPath = SidecarPath(datumPath);
        using DatumFileWriterV2 writer = new(datumPath, sidecarPath);
        writer.SetTypeRegistry(writerRegistry);
        writer.Initialize([column]);
        writer.WriteRowBatch(batch);
        writer.FinalizeWriter();
    }

    /// <summary>
    /// Replays the table-provider's read-side type-table load logic
    /// against an arbitrary <see cref="TypeRegistry"/>: for each entry,
    /// reads the descriptor blob from the sidecar, deserializes-and-interns
    /// into the registry, and records the on-disk → runtime mapping.
    /// </summary>
    private Dictionary<ushort, ushort> LoadTypeTable(
        DatumFileReaderV2 reader, string datumPath, TypeRegistry registry)
    {
        if (reader.Footer.TypeTable.Count == 0) return new();

        using SidecarReadStore sidecar = SidecarReadStore.OpenWithoutFingerprintCheck(SidecarPath(datumPath));
        Dictionary<ushort, ushort> map = new(reader.Footer.TypeTable.Count);
        foreach (TypeTableEntryV5 entry in reader.Footer.TypeTable)
        {
            ReadOnlySpan<byte> blob = sidecar.Read(entry.SidecarOffset, entry.DescriptorLength);
            int runtimeId = TypeDescriptorSerializer.DeserializeAndIntern(blob, registry);
            map[entry.OnDiskTypeId] = checked((ushort)runtimeId);
        }
        return map;
    }

    /// <summary>
    /// Convenience over <see cref="LoadTypeTable"/> that pulls the runtime
    /// TypeId for a specific Struct column out of the column footer's
    /// <c>StructTypeId</c>. Used by the flat-struct test to feed the
    /// page decoder the right id without computing it manually.
    /// </summary>
    private ushort LoadTypeTableAndResolveColumn(
        DatumFileReaderV2 reader, string datumPath, int columnIndex, TypeRegistry registry)
    {
        Dictionary<ushort, ushort> onDiskToRuntime = LoadTypeTable(reader, datumPath, registry);
        ushort? onDiskColumnId = reader.Footer.Columns[columnIndex].StructTypeId;
        Assert.NotNull(onDiskColumnId);
        return onDiskToRuntime[onDiskColumnId!.Value];
    }

    private static string SidecarPath(string datumPath) => datumPath + "-blob";
}
