using System.Buffers.Binary;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.DatumFile.V2;

/// <summary>
/// PR1 tests for the v4 format additions: header version stamp, footer
/// prologue defaults (generation, writerId, baseGeneration,
/// tombstoneGranularity, columnCount, file table, chapter tombstone
/// offsets), the new flag bits, and rejection of older format versions.
///
/// Existing round-trip coverage in <see cref="DatumFileV2RoundTripTests"/>
/// is the load-bearing test for "every encoder still encodes/decodes
/// correctly under v4." This file is the dedicated coverage for the new
/// metadata.
/// </summary>
public sealed class DatumFormatV4FieldTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_v4_{Guid.NewGuid():N}");

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
    public void HeaderStamp_IsVersion4()
    {
        // Read the header magic+version directly off disk so the test
        // pins the on-disk byte, not the constant.
        string path = WriteSimpleFile("version_stamp.datum");

        Span<byte> headerBytes = stackalloc byte[DatumFormatV2.HeaderSize];
        using (FileStream fs = File.OpenRead(path))
        {
            fs.ReadExactly(headerBytes);
        }

        Assert.True(headerBytes[..4].SequenceEqual("DTMF"u8),
            "magic should be 'DTMF'");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[4..6]);
        // The writer always stamps the latest version. Reader-side compat
        // (accepting v4) is exercised in TypeTablePersistenceTests; here
        // we just assert the writer is at the current version.
        Assert.Equal(DatumFormatV2.FormatVersion, version);
    }

    [Fact]
    public void FooterPrologue_DefaultsForFreshFile()
    {
        string path = WriteSimpleFile("prologue_defaults.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        FooterPrologueV4 p = reader.Footer.Prologue;
        Assert.Equal(1UL, p.Generation);            // first write
        // PR3+: WriterId is stamped from WriterIdentity.Default by every
        // commit (initial-write or append). The exact value varies per
        // process; we only require it's non-zero (Anonymous is reserved).
        Assert.NotEqual(WriterIdentity.Anonymous, p.WriterId);
        Assert.Equal(0UL, p.BaseGeneration);        // no prior commit
        Assert.Equal(DatumFormatV2.TombstoneGranularityChapter, p.TombstoneGranularity);
        Assert.Equal(reader.Footer.Columns.Count, p.ColumnCount);
        Assert.Empty(p.FileTableEntries);
        Assert.Empty(p.ChapterTombstoneOffsets);
    }

    [Fact]
    public void Header_ColumnCount_MatchesFooterPrologue()
    {
        // The header's ColumnCount is informational in v4 but the writer
        // still keeps it in sync. Mismatch detection only fires for
        // future column-add commits (PR4); today the values agree.
        string path = WriteSimpleFile("column_count_match.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        Assert.Equal(reader.Header.ColumnCount, reader.Footer.Prologue.ColumnCount);
    }

    [Fact]
    public void NewFlagBits_AreClearOnPr1Writes()
    {
        // HasExternalPages and HasTombstones are forward-compat flags
        // that should never be set by a PR1 writer.
        string path = WriteSimpleFile("flags_clear.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        Assert.Equal(DatumFileFlagsV2.None,
            reader.Header.Flags & DatumFileFlagsV2.HasExternalPages);
        Assert.Equal(DatumFileFlagsV2.None,
            reader.Header.Flags & DatumFileFlagsV2.HasTombstones);
    }

    [Fact]
    public void PageDescriptor_FileId_IsLocalForPr1Writes()
    {
        string path = WriteSimpleFile("local_fileid.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        foreach (ColumnFooterV2 column in reader.Footer.Columns)
        {
            foreach (PageDescriptorV2 page in column.Pages)
            {
                Assert.Equal(DatumFormatV2.LocalFileId, page.FileId);
            }
        }
    }

    [Fact]
    public void ColumnFlags_TombstonedBit_IsClearOnPr1Writes()
    {
        // Tombstoned is reserved as 0x08 but unused until PR4. Round-trip
        // shouldn't set it on any column in a freshly-written file. We
        // test by re-opening and checking the schema is fully visible
        // (a soft-dropped column would be hidden from Columns).
        string path = WriteSimpleFile("tombstone_clear.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(path);

        // Two columns in the helper write — both must surface.
        Assert.Equal(2, reader.Footer.Columns.Count);
        Assert.Equal("a", reader.Footer.Columns[0].Descriptor.Name);
        Assert.Equal("b", reader.Footer.Columns[1].Descriptor.Name);
    }

    [Fact]
    public void Reader_RejectsVersion3Files()
    {
        // Synthesize a v3 file by writing a v4 file then patching the
        // version field at offset 4 down to 3. Reader open should throw
        // a clear InvalidDataException naming the version mismatch.
        string path = WriteSimpleFile("downgrade_v3.datum");

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Position = 4;  // FormatVersion field
            Span<byte> v3 = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(v3, 3);
            fs.Write(v3);
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DatumFileReaderV2.Open(path));
        Assert.Contains("version 3", ex.Message);
        // v5 reader accepts a range; v3 falls below the floor.
        Assert.Contains("reader accepts", ex.Message);
    }

    [Fact]
    public void FooterPrologue_RoundTripsThroughBinaryWriter()
    {
        // Direct serialize/deserialize of the prologue type with
        // non-default values, to pin the byte layout and catch any
        // future field-order regression that the higher-level
        // round-trip wouldn't detect (because the writer never emits
        // non-default values today).
        FileTableEntryV4 entry = new(
            FileId: 7,
            RelativePath: "shards/page_pack_001.datum-pack",
            Fingerprint: Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray());

        FooterPrologueV4 prologue = new(
            Generation: 42,
            WriterId: 0xDEADBEEFCAFEBABE,
            BaseGeneration: 41,
            TombstoneGranularity: DatumFormatV2.TombstoneGranularityChapter,
            ColumnCount: 3,
            FileTableEntries: [entry],
            ChapterTombstoneOffsets: [DatumFormatV2.NoTombstoneBlock, 1024L, 8192L],
            ColumnDefaults: [],
            IdentityColumnIndex: -1,
            IdentitySeed: 0,
            IdentityStep: 0,
            IdentityNextValue: 0,
            IdentityAcceptUserValues: false,
            PrimaryKeyColumnIndices: []);

        using MemoryStream ms = new();
        using (BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            prologue.Serialize(bw);
        }

        ms.Position = 0;
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        FooterPrologueV4 round = FooterPrologueV4.Deserialize(br);

        Assert.Equal(prologue.Generation, round.Generation);
        Assert.Equal(prologue.WriterId, round.WriterId);
        Assert.Equal(prologue.BaseGeneration, round.BaseGeneration);
        Assert.Equal(prologue.TombstoneGranularity, round.TombstoneGranularity);
        Assert.Equal(prologue.ColumnCount, round.ColumnCount);
        Assert.Single(round.FileTableEntries);
        Assert.Equal(7, round.FileTableEntries[0].FileId);
        Assert.Equal("shards/page_pack_001.datum-pack", round.FileTableEntries[0].RelativePath);
        Assert.Equal(entry.Fingerprint, round.FileTableEntries[0].Fingerprint);
        Assert.Equal(prologue.ChapterTombstoneOffsets, round.ChapterTombstoneOffsets);
    }

    [Fact]
    public void FooterPrologue_RejectsUnknownTombstoneGranularity()
    {
        // Pin the contract: deserializer raises InvalidDataException for
        // any tombstoneGranularity other than chapter (1). Page-level
        // (0) is reserved for the future and must not silently parse.
        using MemoryStream ms = new();
        using (BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((ulong)1);  // generation
            bw.Write((ulong)0);  // writerId
            bw.Write((ulong)0);  // baseGeneration
            bw.Write((byte)0);   // tombstoneGranularity = page (reserved!)
            bw.Write(0);         // columnCount
            bw.Write(0);         // fileTableEntryCount
            bw.Write(0);         // chapterTombstoneCount
        }

        ms.Position = 0;
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => FooterPrologueV4.Deserialize(br));
        Assert.Contains("tombstone granularity", ex.Message);
    }

    [Fact]
    public void PageDescriptor_FileIdRoundTrips()
    {
        // The PageDescriptor's FileId field round-trips through the
        // serializer regardless of value. PR1 writers always emit 0;
        // this test ensures the byte slot is there and read back
        // intact for future PR7 use.
        DatumZoneMap zoneMap = new(nullCount: 0, DataKind.Int32, 1, 100);
        PageDescriptorV2 original = new(
            FileId: 12345,
            PageOffset: 0xCAFEBABE,
            PageByteLength: 4096,
            RowCount: 1024,
            ZoneMap: zoneMap);

        using MemoryStream ms = new();
        using (BinaryWriter bw = new(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            original.Serialize(bw);
        }

        ms.Position = 0;
        using BinaryReader br = new(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        PageDescriptorV2 round = PageDescriptorV2.Deserialize(br);

        Assert.Equal(original.FileId, round.FileId);
        Assert.Equal(original.PageOffset, round.PageOffset);
        Assert.Equal(original.PageByteLength, round.PageByteLength);
        Assert.Equal(original.RowCount, round.RowCount);
    }

    // ──────────────────── Helpers ────────────────────

    private string WriteSimpleFile(string fileName)
    {
        ColumnDescriptorV2 colA = new("a", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);
        ColumnDescriptorV2 colB = new("b", DataKind.Int32, EncoderKind.FixedWidth, IsNullable: false);

        Pool pool = CreatePool();
        ColumnLookup lookup = new(["a", "b"]);
        Arena arena = new();
        RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);

        for (int i = 0; i < 3; i++)
        {
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(i);
            row[1] = DataValue.FromInt32(i * 10);
            batch.Add(row);
        }

        string path = Path.Combine(_tempDir, fileName);
        using (DatumFileWriterV2 writer = new(path, sidecarPath: null))
        {
            writer.Initialize([colA, colB]);
            writer.WriteRowBatch(batch);
            writer.FinalizeWriter();
        }
        return path;
    }
}
