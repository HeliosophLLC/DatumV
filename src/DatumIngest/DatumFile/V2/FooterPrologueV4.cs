namespace DatumIngest.DatumFile.V2;

/// <summary>
/// Footer prologue introduced in format v4. Sits at the start of the
/// footer body, before the per-column blocks, and carries metadata that
/// applies to the file as a whole (commit lineage, multi-writer
/// stamping, file-table for cross-file pages, chapter tombstone offset
/// table).
/// </summary>
/// <remarks>
/// <para>
/// Most fields are passive in v4 PR1 — the writer emits zeros / empty
/// lists, and capabilities that consume them ship in later PRs:
/// </para>
/// <list type="bullet">
///   <item><see cref="Generation"/>: starts at <c>1</c> on first write, increments per commit (PR2 wires append-time bumps).</item>
///   <item><see cref="WriterId"/>: <c>0</c> today; populated when multi-writer stamping turns on (PR3).</item>
///   <item><see cref="BaseGeneration"/>: <c>0</c> today; <c>generation - 1</c> in append/delete commits (PR2/PR5).</item>
///   <item><see cref="FileTableEntries"/>: empty today; cross-file pages ship in PR7.</item>
///   <item><see cref="ChapterTombstoneOffsets"/>: empty today; soft-delete ships in PR5.</item>
/// </list>
/// </remarks>
/// <param name="Generation">
/// Monotonic commit counter. <c>1</c> for the initial write of a new
/// file, incremented by every subsequent commit (append, delete, drop
/// column, add column, compaction). Lets callers / future MVCC layers
/// detect "did the file change since I cached this footer?".
/// </param>
/// <param name="WriterId">
/// Identifies the writer that produced this commit. Always <c>0</c> in
/// PR1. When multi-writer stamping turns on, each writer instance
/// supplies a stable id; readers ignore the value, it's purely for
/// audit / conflict diagnosis.
/// </param>
/// <param name="BaseGeneration">
/// The <see cref="Generation"/> the writer read when it started preparing
/// this commit. Always <c>generation - 1</c> in single-writer flow;
/// can equal a generation older than <c>generation - 1</c> when a
/// multi-writer commit re-bases on top of a concurrent winner.
/// </param>
/// <param name="TombstoneGranularity">
/// Pinned to <see cref="DatumFormatV2.TombstoneGranularityChapter"/>
/// (<c>1</c>) in v4. Reserved for an eventual page-level granularity
/// (<c>0</c>) if measurements ever justify finer mutation grain.
/// </param>
/// <param name="ColumnCount">
/// Authoritative column count for the footer body (the per-column
/// block loop runs this many times). Header's <c>ColumnCount</c> field
/// is informational only in v4 — when the footer prologue and header
/// disagree (e.g., during a column-add commit window), the prologue
/// wins.
/// </param>
/// <param name="FileTableEntries">
/// Cross-file page references. Empty in PR1 — the writer never emits a
/// non-zero <see cref="PageDescriptorV2.FileId"/> until cross-file
/// rewrite ships in PR7.
/// </param>
/// <param name="ChapterTombstoneOffsets">
/// One offset per chapter in the file (so length, when non-empty,
/// equals the file's chapter count = <c>⌈totalRowCount /
/// (PagesPerChapter × pageSize)⌉</c>). Each element is either
/// <see cref="DatumFormatV2.NoTombstoneBlock"/> (<c>-1</c>, no
/// tombstones in that chapter) or the absolute file offset of the 8 KiB
/// chapter tombstone bitmap. Empty list in PR1.
/// </param>
/// <param name="ColumnDefaults">
/// Per-column <c>DEFAULT</c> literal expressions captured from the
/// table's <c>CREATE TABLE</c> definition. Stored as a SQL fragment per
/// entry (round-tripped through <c>QueryExplainer.FormatExpression</c>
/// at write time and the parser at read time) so the prologue does not
/// need a binary <c>DataValue</c> serializer. Empty when no column has
/// a declared default. Not every column index needs an entry — entries
/// without a matching <see cref="ColumnDefaultV4.ColumnIndex"/> have no
/// default.
/// </param>
public sealed record FooterPrologueV4(
    ulong Generation,
    ulong WriterId,
    ulong BaseGeneration,
    byte TombstoneGranularity,
    int ColumnCount,
    IReadOnlyList<FileTableEntryV4> FileTableEntries,
    IReadOnlyList<long> ChapterTombstoneOffsets,
    IReadOnlyList<ColumnDefaultV4> ColumnDefaults)
{
    /// <summary>
    /// Default prologue for a fresh single-writer commit with no
    /// tombstones and no external pages. Generation starts at <c>1</c>;
    /// <c>baseGeneration</c> is <c>0</c> (no prior commit).
    /// </summary>
    public static FooterPrologueV4 InitialFor(int columnCount) => new(
        Generation: 1,
        WriterId: 0,
        BaseGeneration: 0,
        TombstoneGranularity: DatumFormatV2.TombstoneGranularityChapter,
        ColumnCount: columnCount,
        FileTableEntries: Array.Empty<FileTableEntryV4>(),
        ChapterTombstoneOffsets: Array.Empty<long>(),
        ColumnDefaults: Array.Empty<ColumnDefaultV4>());

    /// <summary>
    /// Serializes the prologue. Layout:
    /// <list type="bullet">
    ///   <item>generation (8) + writerId (8) + baseGeneration (8)</item>
    ///   <item>tombstoneGranularity (1)</item>
    ///   <item>columnCount (4)</item>
    ///   <item>fileTableEntryCount (4) + entries[]</item>
    ///   <item>chapterTombstoneCount (4) + int64 offsets[]</item>
    ///   <item>columnDefaultCount (4) + entries[]</item>
    /// </list>
    /// </summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(Generation);
        writer.Write(WriterId);
        writer.Write(BaseGeneration);
        writer.Write(TombstoneGranularity);
        writer.Write(ColumnCount);

        writer.Write(FileTableEntries.Count);
        foreach (FileTableEntryV4 entry in FileTableEntries)
        {
            entry.Serialize(writer);
        }

        writer.Write(ChapterTombstoneOffsets.Count);
        foreach (long offset in ChapterTombstoneOffsets)
        {
            writer.Write(offset);
        }

        writer.Write(ColumnDefaults.Count);
        foreach (ColumnDefaultV4 entry in ColumnDefaults)
        {
            entry.Serialize(writer);
        }
    }

    /// <summary>Deserializes a prologue written by <see cref="Serialize"/>.</summary>
    internal static FooterPrologueV4 Deserialize(BinaryReader reader)
    {
        ulong generation = reader.ReadUInt64();
        ulong writerId = reader.ReadUInt64();
        ulong baseGeneration = reader.ReadUInt64();
        byte tombstoneGranularity = reader.ReadByte();
        if (tombstoneGranularity != DatumFormatV2.TombstoneGranularityChapter)
        {
            throw new InvalidDataException(
                $"Unsupported tombstone granularity {tombstoneGranularity}; expected " +
                $"{DatumFormatV2.TombstoneGranularityChapter} (chapter).");
        }

        int columnCount = reader.ReadInt32();
        if (columnCount < 0)
        {
            throw new InvalidDataException(
                $"Footer prologue declares negative column count ({columnCount}).");
        }

        int fileTableCount = reader.ReadInt32();
        if (fileTableCount < 0)
        {
            throw new InvalidDataException(
                $"Footer prologue declares negative file-table entry count ({fileTableCount}).");
        }
        FileTableEntryV4[] fileTable = new FileTableEntryV4[fileTableCount];
        for (int i = 0; i < fileTableCount; i++)
        {
            fileTable[i] = FileTableEntryV4.Deserialize(reader);
        }

        int chapterTombstoneCount = reader.ReadInt32();
        if (chapterTombstoneCount < 0)
        {
            throw new InvalidDataException(
                $"Footer prologue declares negative chapter-tombstone count ({chapterTombstoneCount}).");
        }
        long[] chapterTombstoneOffsets = new long[chapterTombstoneCount];
        for (int i = 0; i < chapterTombstoneCount; i++)
        {
            chapterTombstoneOffsets[i] = reader.ReadInt64();
        }

        int columnDefaultCount = reader.ReadInt32();
        if (columnDefaultCount < 0)
        {
            throw new InvalidDataException(
                $"Footer prologue declares negative column-default count ({columnDefaultCount}).");
        }
        ColumnDefaultV4[] columnDefaults = new ColumnDefaultV4[columnDefaultCount];
        for (int i = 0; i < columnDefaultCount; i++)
        {
            columnDefaults[i] = ColumnDefaultV4.Deserialize(reader);
        }

        return new FooterPrologueV4(
            generation, writerId, baseGeneration,
            tombstoneGranularity, columnCount,
            fileTable, chapterTombstoneOffsets,
            columnDefaults);
    }
}

/// <summary>
/// A persisted <c>DEFAULT</c> literal for one column. Captured at
/// <c>CREATE TABLE</c> time and round-tripped on read so a freshly-opened
/// catalog (and standalone <c>.datum</c> tools) see the same defaults
/// without consulting <c>.datum-catalog.json</c>.
/// </summary>
/// <param name="ColumnIndex">
/// Index of the column whose default this entry carries (matches the
/// schema's column order and the column footer index).
/// </param>
/// <param name="SqlFragment">
/// The default expression rendered as a SQL fragment via
/// <c>QueryExplainer.FormatExpression</c>. Re-parsed on read with
/// <c>SqlParser.Parse("SELECT &lt;fragment&gt;")</c> — the same trick
/// used by UDF default-parameter persistence — so we never need a
/// dedicated binary <c>DataValue</c> serializer for the prologue.
/// </param>
public sealed record ColumnDefaultV4(
    ushort ColumnIndex,
    string SqlFragment)
{
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(ColumnIndex);
        writer.Write(SqlFragment);
    }

    internal static ColumnDefaultV4 Deserialize(BinaryReader reader)
    {
        ushort columnIndex = reader.ReadUInt16();
        string fragment = reader.ReadString();
        return new ColumnDefaultV4(columnIndex, fragment);
    }
}

/// <summary>
/// One row of the footer prologue's file table. Maps a non-zero
/// <see cref="PageDescriptorV2.FileId"/> to a relative path (resolved
/// against the primary <c>.datum</c> file's directory) plus a
/// fingerprint that lets the reader detect path collisions / staleness
/// when external files are deleted-and-recreated under the same name.
/// </summary>
/// <param name="FileId">
/// Stable per-file identifier, matching the <see cref="PageDescriptorV2.FileId"/>
/// values stamped in page descriptors. <c>0</c> is reserved for the
/// primary file and never appears in the table.
/// </param>
/// <param name="RelativePath">
/// Path relative to the primary <c>.datum</c> file's directory. Forward
/// slashes; readers normalize for the host OS.
/// </param>
/// <param name="Fingerprint">
/// 16-byte identity stamp of the external file. Set by the writer that
/// produced the external file; verified by the reader on open. A
/// mismatch means the path has been recreated or replaced and the
/// manifest is stale.
/// </param>
public sealed record FileTableEntryV4(
    ushort FileId,
    string RelativePath,
    byte[] Fingerprint)
{
    /// <summary>Length of the fingerprint in bytes.</summary>
    public const int FingerprintBytes = 16;

    internal void Serialize(BinaryWriter writer)
    {
        if (Fingerprint.Length != FingerprintBytes)
        {
            throw new InvalidOperationException(
                $"FileTableEntryV4.Fingerprint must be exactly {FingerprintBytes} bytes (got {Fingerprint.Length}).");
        }
        writer.Write(FileId);
        writer.Write(RelativePath);
        writer.Write(Fingerprint);
    }

    internal static FileTableEntryV4 Deserialize(BinaryReader reader)
    {
        ushort fileId = reader.ReadUInt16();
        string path = reader.ReadString();
        byte[] fingerprint = reader.ReadBytes(FingerprintBytes);
        if (fingerprint.Length != FingerprintBytes)
        {
            throw new InvalidDataException(
                $"File-table entry truncated: expected {FingerprintBytes}-byte fingerprint, got {fingerprint.Length}.");
        }
        return new FileTableEntryV4(fileId, path, fingerprint);
    }
}
