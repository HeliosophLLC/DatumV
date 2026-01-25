using System.Buffers.Binary;
using System.IO.Hashing;
using DatumIngest.Indexing.BTree.Mutable;

namespace DatumIngest.Indexing.BTree.MutableBytes;

/// <summary>
/// One header slot of a bytes-keyed mutable B+Tree file. Mirrors
/// <see cref="MutableBPlusTreeHeader"/> but uses a distinct file magic
/// (<see cref="BytesFileMagic"/>) so typed and bytes-keyed trees can't
/// be confused at open time, and drops the <c>KeyKind</c> field (bytes
/// trees have no per-column kind — the encoder is the source of truth).
/// </summary>
internal readonly record struct MutableBPlusTreeBytesHeader(
    long CommitGen,
    uint RootPageId,
    uint FreeListHead,
    uint PageCount,
    ushort TreeHeight,
    long EntryCount,
    bool AllowDuplicates)
{
    /// <summary>
    /// File magic for bytes-keyed mutable B+Tree files ("BKBT" little-endian).
    /// Distinct from the typed-tree magic <c>PKBT</c> so a reader catches a
    /// file-format mismatch instead of mis-parsing a typed tree as bytes
    /// (or vice versa).
    /// </summary>
    internal const uint BytesFileMagic = 0x54424B42; // 'B' 'K' 'B' 'T'

    /// <summary>Current on-disk format version for the bytes-keyed tree.</summary>
    internal const uint CurrentVersion = 1;

    /// <summary>
    /// Returns the empty-tree header for a fresh file. Both slots are written
    /// with this state at <c>Create</c> time (gen=0 and gen=1) so reader open
    /// is deterministic regardless of which slot is sampled first.
    /// </summary>
    internal static MutableBPlusTreeBytesHeader Empty(long commitGen, bool allowDuplicates = false) => new(
        CommitGen: commitGen,
        RootPageId: MutableBPlusTreeConstants.NoLinkedPage,
        FreeListHead: MutableBPlusTreeConstants.NoLinkedPage,
        PageCount: 0,
        TreeHeight: 0,
        EntryCount: 0,
        AllowDuplicates: allowDuplicates);

    /// <summary>
    /// Encodes this header into the 256-byte slot buffer with a trailing CRC32
    /// over bytes [0..251]. The reader rejects slots whose CRC doesn't match —
    /// that's how torn writes are detected.
    /// </summary>
    internal void WriteTo(Span<byte> destination)
    {
        if (destination.Length < MutableBPlusTreeConstants.HeaderSlotSize)
        {
            throw new ArgumentException(
                $"Header destination must be at least {MutableBPlusTreeConstants.HeaderSlotSize} bytes; got {destination.Length}.",
                nameof(destination));
        }

        destination[..MutableBPlusTreeConstants.HeaderSlotSize].Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], BytesFileMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], CurrentVersion);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..16], CommitGen);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], RootPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], FreeListHead);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], PageCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[28..30], TreeHeight);
        BinaryPrimitives.WriteInt64LittleEndian(destination[30..38], EntryCount);
        destination[38] = AllowDuplicates ? (byte)1 : (byte)0;

        // Bytes [39..251] are reserved; left zero by the Clear above.

        uint crc = Crc32.HashToUInt32(destination[..(MutableBPlusTreeConstants.HeaderSlotSize - 4)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[(MutableBPlusTreeConstants.HeaderSlotSize - 4)..MutableBPlusTreeConstants.HeaderSlotSize],
            crc);
    }

    /// <summary>
    /// Tries to decode a header slot. Returns <see langword="false"/> when the
    /// magic, version, or CRC is invalid. Callers fall back to the other slot.
    /// </summary>
    internal static bool TryReadFrom(ReadOnlySpan<byte> source, out MutableBPlusTreeBytesHeader header)
    {
        header = default;

        if (source.Length < MutableBPlusTreeConstants.HeaderSlotSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);
        if (magic != BytesFileMagic) return false;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]);
        if (version != CurrentVersion) return false;

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            source[(MutableBPlusTreeConstants.HeaderSlotSize - 4)..MutableBPlusTreeConstants.HeaderSlotSize]);
        uint computedCrc = Crc32.HashToUInt32(source[..(MutableBPlusTreeConstants.HeaderSlotSize - 4)]);
        if (storedCrc != computedCrc) return false;

        header = new MutableBPlusTreeBytesHeader(
            CommitGen: BinaryPrimitives.ReadInt64LittleEndian(source[8..16]),
            RootPageId: BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]),
            FreeListHead: BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]),
            PageCount: BinaryPrimitives.ReadUInt32LittleEndian(source[24..28]),
            TreeHeight: BinaryPrimitives.ReadUInt16LittleEndian(source[28..30]),
            EntryCount: BinaryPrimitives.ReadInt64LittleEndian(source[30..38]),
            AllowDuplicates: source[38] != 0);

        return true;
    }
}
