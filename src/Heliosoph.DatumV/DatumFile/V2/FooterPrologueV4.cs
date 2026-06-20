namespace Heliosoph.DatumV.DatumFile.V2;

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
/// <param name="IdentityColumnIndex">
/// Index of the table's <c>IDENTITY</c> column, or <c>-1</c> when no
/// column carries IDENTITY. At most one IDENTITY column per table —
/// validated at <c>CREATE TABLE</c> time. Always <c>-1</c> in files
/// written by binaries that predate PR10e (the missing trailing field
/// reads back as zero/-1; on next commit the writer stamps a real
/// value).
/// </param>
/// <param name="IdentitySeed">
/// First value the IDENTITY counter produced; captured at
/// <c>CREATE TABLE</c> time and never changes. Meaningless when
/// <see cref="IdentityColumnIndex"/> is <c>-1</c>.
/// </param>
/// <param name="IdentityStep">
/// Increment applied after each generated IDENTITY value. Must be
/// non-zero; meaningless when <see cref="IdentityColumnIndex"/> is
/// <c>-1</c>.
/// </param>
/// <param name="IdentityNextValue">
/// The value the next INSERT-driven reservation would return.
/// Initially equals <see cref="IdentitySeed"/>; advanced by every
/// commit that fills IDENTITY rows. Persisted on the prologue so a
/// reopen (and standalone <c>.datum</c> tools) see the live counter
/// without consulting <c>.datum-catalog.json</c>. Meaningless when
/// <see cref="IdentityColumnIndex"/> is <c>-1</c>.
/// </param>
/// <param name="IdentityAcceptUserValues">
/// <see langword="true"/> when the IDENTITY column was declared
/// <c>GENERATED BY DEFAULT AS IDENTITY</c> — user-supplied values are
/// accepted on INSERT and the counter is only consulted when the
/// column is omitted. <see langword="false"/> for <c>GENERATED ALWAYS
/// AS IDENTITY</c> (and the legacy bare <c>IDENTITY</c> form), which
/// always rejects explicit values. Meaningless when
/// <see cref="IdentityColumnIndex"/> is <c>-1</c>.
/// </param>
/// <param name="PrimaryKeyColumnIndices">
/// Ordered list of footer column indices forming the table's
/// <c>PRIMARY KEY</c>. Empty when the table has no PK. Order matches
/// the user's PK declaration (table-level <c>PRIMARY KEY (b, a)</c>
/// keeps <c>b</c> first regardless of column-declaration order). PR10f
/// caps the total PK key size at 16 bytes (sum of column-kind sizes);
/// the catalog enforces this at <c>CREATE TABLE</c> time.
/// </param>
/// <param name="Extensions">
/// Forward-compat TLV block introduced in v7. Empty when the file
/// carries no extension entries; the block is omitted entirely
/// (gated by <see cref="DatumFileFlagsV2.HasPrologueExtensions"/>) when
/// this list is empty, so files that don't use extensions stay
/// byte-identical to their unextended layout. Each entry pairs an
/// opaque <c>uint16</c> tag with a length-prefixed payload; readers
/// ignore unknown tags so adding new extension entries is a
/// forward-compatible operation.
/// </param>
public sealed record FooterPrologueV4(
    ulong Generation,
    ulong WriterId,
    ulong BaseGeneration,
    byte TombstoneGranularity,
    int ColumnCount,
    IReadOnlyList<FileTableEntryV4> FileTableEntries,
    IReadOnlyList<long> ChapterTombstoneOffsets,
    IReadOnlyList<ColumnDefaultV4> ColumnDefaults,
    short IdentityColumnIndex,
    long IdentitySeed,
    long IdentityStep,
    long IdentityNextValue,
    bool IdentityAcceptUserValues,
    IReadOnlyList<ushort> PrimaryKeyColumnIndices,
    IReadOnlyList<PrologueExtensionV7> Extensions)
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
        ColumnDefaults: Array.Empty<ColumnDefaultV4>(),
        IdentityColumnIndex: -1,
        IdentitySeed: 0,
        IdentityStep: 0,
        IdentityNextValue: 0,
        IdentityAcceptUserValues: false,
        PrimaryKeyColumnIndices: Array.Empty<ushort>(),
        Extensions: Array.Empty<PrologueExtensionV7>());

    /// <summary>
    /// Serializes the prologue. Layout:
    /// <list type="bullet">
    ///   <item>generation (8) + writerId (8) + baseGeneration (8)</item>
    ///   <item>tombstoneGranularity (1)</item>
    ///   <item>columnCount (4)</item>
    ///   <item>fileTableEntryCount (4) + entries[]</item>
    ///   <item>chapterTombstoneCount (4) + int64 offsets[]</item>
    ///   <item>columnDefaultCount (4) + entries[]</item>
    ///   <item>identityColumnIndex (2) + identitySeed (8) + identityStep (8) + identityNextValue (8) + identityAcceptUserValues (1)</item>
    ///   <item>primaryKeyCount (1) + uint16 indices[]</item>
    ///   <item>(when <paramref name="hasExtensions"/>) extensionCount (4) + entries[]</item>
    /// </list>
    /// </summary>
    /// <param name="writer">Binary writer.</param>
    /// <param name="hasExtensions">
    /// Mirrors <see cref="DatumFileFlagsV2.HasPrologueExtensions"/>. When
    /// false, the trailing extensions block is omitted so pre-v7-feature
    /// files stay byte-identical to their unextended layout.
    /// </param>
    internal void Serialize(BinaryWriter writer, bool hasExtensions = false)
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

        writer.Write(IdentityColumnIndex);
        writer.Write(IdentitySeed);
        writer.Write(IdentityStep);
        writer.Write(IdentityNextValue);
        writer.Write(IdentityAcceptUserValues);

        // PR10f caps PK column count at 16 (a fixed-size byte budget,
        // not a column count, but in practice 16 columns is more than
        // enough). Stored as a single byte to keep the prologue tight.
        writer.Write(checked((byte)PrimaryKeyColumnIndices.Count));
        foreach (ushort columnIndex in PrimaryKeyColumnIndices)
        {
            writer.Write(columnIndex);
        }

        if (hasExtensions)
        {
            writer.Write(Extensions.Count);
            foreach (PrologueExtensionV7 entry in Extensions)
            {
                entry.Serialize(writer);
            }
        }
    }

    /// <summary>Deserializes a prologue written by <see cref="Serialize"/>.</summary>
    /// <param name="reader">Binary reader.</param>
    /// <param name="hasExtensions">
    /// Mirrors <see cref="DatumFileFlagsV2.HasPrologueExtensions"/>.
    /// Controls whether the trailing extensions block is consumed.
    /// </param>
    internal static FooterPrologueV4 Deserialize(BinaryReader reader, bool hasExtensions = false)
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

        short identityColumnIndex = reader.ReadInt16();
        long identitySeed = reader.ReadInt64();
        long identityStep = reader.ReadInt64();
        long identityNextValue = reader.ReadInt64();
        bool identityAcceptUserValues = reader.ReadBoolean();

        byte primaryKeyCount = reader.ReadByte();
        ushort[] primaryKeyColumnIndices = new ushort[primaryKeyCount];
        for (int i = 0; i < primaryKeyCount; i++)
        {
            primaryKeyColumnIndices[i] = reader.ReadUInt16();
        }

        IReadOnlyList<PrologueExtensionV7> extensions;
        if (hasExtensions)
        {
            int extensionCount = reader.ReadInt32();
            if (extensionCount < 0)
            {
                throw new InvalidDataException(
                    $"Footer prologue declares negative extension count ({extensionCount}).");
            }
            PrologueExtensionV7[] entries = new PrologueExtensionV7[extensionCount];
            for (int i = 0; i < extensionCount; i++)
            {
                entries[i] = PrologueExtensionV7.Deserialize(reader);
            }
            extensions = entries;
        }
        else
        {
            extensions = Array.Empty<PrologueExtensionV7>();
        }

        return new FooterPrologueV4(
            generation, writerId, baseGeneration,
            tombstoneGranularity, columnCount,
            fileTable, chapterTombstoneOffsets,
            columnDefaults,
            identityColumnIndex,
            identitySeed,
            identityStep,
            identityNextValue,
            identityAcceptUserValues,
            primaryKeyColumnIndices,
            extensions);
    }
}

/// <summary>
/// One entry of the v7 footer-prologue extensions TLV block. Reserved
/// escape hatch for future file-level scalars that don't justify their
/// own dedicated flag bit and field (collation ids, tenant ids,
/// schema-evolution log offsets, etc.). Readers ignore unknown
/// <see cref="Tag"/> values so writers can add new extension entries
/// without invalidating older readers.
/// </summary>
/// <param name="Tag">
/// Opaque 16-bit identifier of the extension entry. Allocation policy
/// is "first writer claims an unused tag and documents it"; the format
/// itself does not enforce any tag-to-payload contract. No tags are
/// allocated today.
/// </param>
/// <param name="Payload">
/// Length-prefixed opaque payload. Up to 4 GiB; sized for arbitrary
/// blobs even though realistic entries are expected to be short
/// (handful of bytes to a few KiB).
/// </param>
public sealed record PrologueExtensionV7(
    ushort Tag,
    byte[] Payload)
{
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Payload.Length);
        writer.Write(Payload);
    }

    internal static PrologueExtensionV7 Deserialize(BinaryReader reader)
    {
        ushort tag = reader.ReadUInt16();
        int length = reader.ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException(
                $"Prologue extension declares negative payload length ({length}).");
        }
        byte[] payload = reader.ReadBytes(length);
        if (payload.Length != length)
        {
            throw new InvalidDataException(
                $"Prologue extension payload truncated: expected {length} bytes, got {payload.Length}.");
        }
        return new PrologueExtensionV7(tag, payload);
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
/// One row of the footer's optional computed-columns table (v6+). Pairs a
/// column index with the SQL fragment the catalog re-parses to recover
/// the column's <c>GENERATED ALWAYS AS (...)</c> expression. The flag
/// <see cref="DatumFileFlagsV2.HasColumnComputeds"/> gates whether the
/// trailing block exists; absent flag means no computed columns.
/// </summary>
/// <param name="ColumnIndex">
/// Index of the column in the schema (0-based) whose value is derived
/// from the expression.
/// </param>
/// <param name="SqlFragment">
/// The expression rendered as SQL via <c>QueryExplainer.FormatExpression</c>.
/// Re-parsed on read with <c>SqlParser.Parse("SELECT &lt;fragment&gt;")</c>,
/// matching the <see cref="ColumnDefaultV4"/> persistence pattern.
/// </param>
public sealed record ColumnComputedV4(
    ushort ColumnIndex,
    string SqlFragment)
{
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(ColumnIndex);
        writer.Write(SqlFragment);
    }

    internal static ColumnComputedV4 Deserialize(BinaryReader reader)
    {
        ushort columnIndex = reader.ReadUInt16();
        string fragment = reader.ReadString();
        return new ColumnComputedV4(columnIndex, fragment);
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
