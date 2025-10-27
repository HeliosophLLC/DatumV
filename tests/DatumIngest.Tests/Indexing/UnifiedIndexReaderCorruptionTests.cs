using System.Buffers.Binary;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Validates that <see cref="UnifiedIndexReader"/> rejects corrupted v5 unified
/// <c>.datum-index</c> files with clean exceptions rather than undefined behavior.
/// </summary>
public sealed class UnifiedIndexReaderCorruptionTests : ServiceTestBase
{
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for corrupt test files.</summary>
    public UnifiedIndexReaderCorruptionTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(), "UnifiedIndexCorruptionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>Removes the temporary directory and all test files.</summary>
    public override void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        base.Dispose();
    }

    // ─────────────────────── Header corruption ───────────────────────

    [Fact]
    public void Open_EmptyFile_Throws()
    {
        string path = WriteTempFile("empty.datum-index", []);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TooShortForHeader_ThrowsInvalidDataException()
    {
        // Header is 24 bytes; provide only 8. Mmap pads remaining with zeros,
        // so magic = first 4 bytes of the provided data (all zeros) ≠ "DXIX".
        string path = WriteTempFile("short.datum-index", new byte[8]);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_InvalidMagicBytes_ThrowsInvalidDataException()
    {
        string path = WriteTempFile("bad_magic.datum-index", new byte[64]);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_UnsupportedVersion_ThrowsInvalidDataException()
    {
        byte[] data = new byte[64];
        "DXIX"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 99);

        string path = WriteTempFile("bad_version.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_VersionZero_ThrowsInvalidDataException()
    {
        byte[] data = new byte[64];
        "DXIX"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 0);

        string path = WriteTempFile("version_zero.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_AllOnesFile_ThrowsInvalidDataException()
    {
        byte[] data = Enumerable.Repeat((byte)0xFF, 128).ToArray();
        string path = WriteTempFile("all_ones.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    // ──────────────── Section directory corruption ────────────────

    [Fact]
    public void Open_NegativeSectionCount_ThrowsInvalidDataException()
    {
        byte[] data = BuildHeaderOnly(sectionCount: -1);
        string path = WriteTempFile("negative_count.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_HugeSectionCount_ThrowsInvalidDataException()
    {
        byte[] data = BuildHeaderOnly(sectionCount: int.MaxValue);
        string path = WriteTempFile("huge_count.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_ZeroSections_ThrowsInvalidDataException()
    {
        // Zero sections means no Fingerprint or TableDirectory → FindSection throws.
        byte[] data = BuildHeaderOnly(sectionCount: 0);
        string path = WriteTempFile("zero_sections.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedDirectory_ThrowsInvalidDataException()
    {
        // Claims 10 sections (needs 24 + 180 = 204 bytes) but file is only 48 bytes.
        byte[] data = BuildHeaderOnly(sectionCount: 10, totalSize: 48);
        string path = WriteTempFile("truncated_dir.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_SectionOffsetBeyondFile_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        // Corrupt the first directory entry's offset field (bytes 2..9 within the entry)
        // to point far beyond the file.
        int offsetField = UnifiedIndexWriter.HeaderSize + 2;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offsetField), 999_999);

        string path = WriteTempFile("bad_offset.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_SectionLengthExceedsFile_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        // Corrupt the first directory entry's length field (bytes 10..17 within the entry).
        int lengthField = UnifiedIndexWriter.HeaderSize + 10;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(lengthField), 999_999);

        string path = WriteTempFile("bad_length.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_NegativeSectionOffset_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        int offsetField = UnifiedIndexWriter.HeaderSize + 2;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offsetField), -1);

        string path = WriteTempFile("negative_offset.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_NegativeSectionLength_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        int lengthField = UnifiedIndexWriter.HeaderSize + 10;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(lengthField), -42);

        string path = WriteTempFile("negative_length.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    // ─────────────── Missing required sections ───────────────

    [Fact]
    public void Open_MissingFingerprintSection_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        // Overwrite the Fingerprint entry's type byte to an unknown value.
        data[UnifiedIndexWriter.HeaderSize] = 99;

        string path = WriteTempFile("no_fingerprint.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_MissingTableDirectorySection_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        // Overwrite the TableDirectory entry's type to Fingerprint(0) → duplicate FP, no TD.
        data[UnifiedIndexWriter.HeaderSize + UnifiedIndexWriter.DirectoryEntrySize] = 0;

        string path = WriteTempFile("no_tabledir.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_MissingSchemaSection_ThrowsInvalidDataException()
    {
        byte[] data = BuildValidFileBytes();

        // Overwrite the Schema entry's type byte to an unknown value.
        int schemaEntryOffset = UnifiedIndexWriter.HeaderSize
            + 2 * UnifiedIndexWriter.DirectoryEntrySize;
        data[schemaEntryOffset] = 99;

        string path = WriteTempFile("no_schema.datum-index", data);

        Assert.Throws<InvalidDataException>(() => UnifiedIndexReader.Open(path));
    }

    // ──────────────── Truncated section payloads ─────────────────

    [Fact]
    public void Open_AllSectionDataOverwrittenWithGarbage_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Overwrite everything after the header + directory with 0xFF bytes.
        // The directory entries still reference these offsets, but the data is garbage.
        // The table directory reader will fail decoding 7-bit encoded string lengths.
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int directoryEnd = UnifiedIndexWriter.HeaderSize
            + sectionCount * UnifiedIndexWriter.DirectoryEntrySize;
        data.AsSpan(directoryEnd).Fill(0xFF);

        string path = WriteTempFile("garbage_sections.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedTableDirectoryPayload_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Set TableDirectory length to 1 (needs at least 1 + length-prefixed table name).
        // A 1-byte ViewStream allows reading tableCount but not the table name string.
        int lengthField = UnifiedIndexWriter.HeaderSize
            + UnifiedIndexWriter.DirectoryEntrySize + 10;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(lengthField), 1);

        string path = WriteTempFile("truncated_td.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TruncatedSchemaPayload_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Set Schema length to 2 (third directory entry; needs at least 12 bytes).
        int lengthField = UnifiedIndexWriter.HeaderSize
            + 2 * UnifiedIndexWriter.DirectoryEntrySize + 10;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(lengthField), 2);

        string path = WriteTempFile("truncated_schema.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    // ─────────────── Corrupt section internals ───────────────

    [Fact]
    public void Open_SchemaWithHugeColumnCount_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Locate the Schema section's data offset from its directory entry.
        int schemaDirectoryOffset = UnifiedIndexWriter.HeaderSize
            + 2 * UnifiedIndexWriter.DirectoryEntrySize;
        long schemaDataOffset = BinaryPrimitives.ReadInt64LittleEndian(
            data.AsSpan(schemaDirectoryOffset + 2));

        // columnCount sits at schemaDataOffset + 8 (after the i64 totalRowCount).
        BinaryPrimitives.WriteInt32LittleEndian(
            data.AsSpan((int)schemaDataOffset + 8), int.MaxValue);

        string path = WriteTempFile("huge_schema_cols.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_TableDirectoryClaimsTooManyTables_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Locate the TableDirectory section's data offset.
        int tableDirectoryEntryOffset = UnifiedIndexWriter.HeaderSize
            + UnifiedIndexWriter.DirectoryEntrySize;
        long tableDirectoryDataOffset = BinaryPrimitives.ReadInt64LittleEndian(
            data.AsSpan(tableDirectoryEntryOffset + 2));

        // The first byte of the section is tableCount. Set to 200.
        data[(int)tableDirectoryDataOffset] = 200;

        string path = WriteTempFile("too_many_tables.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    [Fact]
    public void Open_GarbageInSchemaColumnNames_Throws()
    {
        byte[] data = BuildValidFileBytes();

        // Locate the Schema section data and overwrite column data with random garbage.
        int schemaDirectoryOffset = UnifiedIndexWriter.HeaderSize
            + 2 * UnifiedIndexWriter.DirectoryEntrySize;
        long schemaDataOffset = BinaryPrimitives.ReadInt64LittleEndian(
            data.AsSpan(schemaDirectoryOffset + 2));
        long schemaLength = BinaryPrimitives.ReadInt64LittleEndian(
            data.AsSpan(schemaDirectoryOffset + 10));

        // Fill the entire schema section with 0xFF bytes.
        data.AsSpan((int)schemaDataOffset, (int)schemaLength).Fill(0xFF);

        string path = WriteTempFile("garbage_schema.datum-index", data);

        Assert.ThrowsAny<Exception>(() => UnifiedIndexReader.Open(path));
    }

    // ───────────────────── Sanity check ─────────────────────

    [Fact]
    public void Open_ValidMinimalFile_Succeeds()
    {
        byte[] data = BuildValidFileBytes();
        string path = WriteTempFile("valid.datum-index", data);

        using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(path);

        Assert.Single(mapped.IndexSet.Tables);
        Assert.True(mapped.IndexSet.Tables.ContainsKey("test"));
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Builds a valid minimal v5 unified index file with one table ("test"),
    /// one column (Int32 "id", non-nullable), and zero rows/chunks.
    /// </summary>
    private static byte[] BuildValidFileBytes()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("id", DataKind.Int32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 0);
        SourceIndex index = new(fingerprint, indexSchema, Array.Empty<IndexChunk>());
        SourceIndexSet indexSet = SourceIndexSet.Create("test", index);

        using MemoryStream stream = new();
        UnifiedIndexWriter.Write(indexSet, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds a header-only byte array with valid magic and version but arbitrary section count.
    /// </summary>
    private static byte[] BuildHeaderOnly(int sectionCount, int totalSize = 64)
    {
        byte[] data = new byte[Math.Max(totalSize, UnifiedIndexWriter.HeaderSize)];
        "DXIX"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), UnifiedIndexWriter.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 0); // Flags.
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), sectionCount);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), totalSize);
        return data;
    }

    private string WriteTempFile(string name, byte[] data)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, data);
        return path;
    }
}
