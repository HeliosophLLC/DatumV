using System.Buffers.Binary;
using System.IO.Hashing;
using DatumIngest.Model;

namespace DatumIngest.Indexing.BTree.Mutable;

/// <summary>
/// One header slot of a <c>.datum-pkindex</c> file. The file has two slots
/// at fixed offsets 0 and 256; commits write to the slot whose stored
/// commit-gen is older. Readers pick the slot whose CRC validates and
/// whose commit-gen is higher. Torn writes leave the previous slot intact.
/// </summary>
internal readonly record struct MutableBPlusTreeHeader(
    long CommitGen,
    uint RootPageId,
    uint FreeListHead,
    uint PageCount,
    ushort TreeHeight,
    long EntryCount,
    DataKind KeyKind,
    bool AllowDuplicates)
{
    /// <summary>
    /// Returns the empty-tree header (no root, no pages, no entries). Used at file
    /// creation. Both slots are written with this state and gen=0/gen=1 so reader
    /// open is well-defined. <paramref name="allowDuplicates"/> (true for
    /// acceleration trees, false for PK-style) is captured in the header so
    /// reopens know which Insert semantics to enforce.
    /// </summary>
    internal static MutableBPlusTreeHeader Empty(DataKind keyKind, long commitGen, bool allowDuplicates = false) => new(
        CommitGen: commitGen,
        RootPageId: MutableBPlusTreeConstants.NoLinkedPage,
        FreeListHead: MutableBPlusTreeConstants.NoLinkedPage,
        PageCount: 0,
        TreeHeight: 0,
        EntryCount: 0,
        KeyKind: keyKind,
        AllowDuplicates: allowDuplicates);

    /// <summary>
    /// Encodes this header into the given 256-byte buffer. The CRC32 over the first
    /// 252 bytes is written into bytes [252..255]. Reader rejects slots whose CRC
    /// doesn't match — that's how torn writes are detected.
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

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], MutableBPlusTreeConstants.FileMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], MutableBPlusTreeConstants.CurrentVersion);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..16], CommitGen);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], RootPageId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], FreeListHead);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], PageCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[28..30], TreeHeight);
        BinaryPrimitives.WriteInt64LittleEndian(destination[30..38], EntryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[38..40], (ushort)KeyKind);
        destination[40] = AllowDuplicates ? (byte)1 : (byte)0;

        // Bytes [41..251] are reserved; left zero by the Clear above.

        uint crc = Crc32.HashToUInt32(destination[..(MutableBPlusTreeConstants.HeaderSlotSize - 4)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[(MutableBPlusTreeConstants.HeaderSlotSize - 4)..MutableBPlusTreeConstants.HeaderSlotSize],
            crc);
    }

    /// <summary>
    /// Tries to decode a header slot. Returns <c>false</c> when the magic, version,
    /// or CRC is invalid (i.e. the slot is torn or never initialized). Callers should
    /// fall back to the other slot.
    /// </summary>
    internal static bool TryReadFrom(ReadOnlySpan<byte> source, out MutableBPlusTreeHeader header)
    {
        header = default;

        if (source.Length < MutableBPlusTreeConstants.HeaderSlotSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]);

        if (magic != MutableBPlusTreeConstants.FileMagic)
        {
            return false;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]);

        if (version != MutableBPlusTreeConstants.CurrentVersion)
        {
            return false;
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            source[(MutableBPlusTreeConstants.HeaderSlotSize - 4)..MutableBPlusTreeConstants.HeaderSlotSize]);
        uint computedCrc = Crc32.HashToUInt32(source[..(MutableBPlusTreeConstants.HeaderSlotSize - 4)]);

        if (storedCrc != computedCrc)
        {
            return false;
        }

        header = new MutableBPlusTreeHeader(
            CommitGen: BinaryPrimitives.ReadInt64LittleEndian(source[8..16]),
            RootPageId: BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]),
            FreeListHead: BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]),
            PageCount: BinaryPrimitives.ReadUInt32LittleEndian(source[24..28]),
            TreeHeight: BinaryPrimitives.ReadUInt16LittleEndian(source[28..30]),
            EntryCount: BinaryPrimitives.ReadInt64LittleEndian(source[30..38]),
            KeyKind: (DataKind)BinaryPrimitives.ReadUInt16LittleEndian(source[38..40]),
            AllowDuplicates: source[40] != 0);

        return true;
    }
}
