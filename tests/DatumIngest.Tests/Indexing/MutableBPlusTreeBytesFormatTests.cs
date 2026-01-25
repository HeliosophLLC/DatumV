using System.Buffers.Binary;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Indexing.BTree.MutableBytes;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// File-format-specific tests for the bytes-keyed
/// <see cref="MutableBPlusTreeBytes"/>: corrupted-header detection, torn-write
/// recovery, dual-slot fallback. Mirrors
/// <see cref="MutableBPlusTreeFormatTests"/> for the typed tree — same
/// dual-slot / CRC scaffolding, different file magic.
/// </summary>
public sealed class MutableBPlusTreeBytesFormatTests : IDisposable
{
    private readonly string _tempDir;

    public MutableBPlusTreeBytesFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MutableBytesTreeFormat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name + ".datum-bytespkindex");

    [Fact]
    public void Open_FailsOnCorruptFile()
    {
        string path = PathFor("corrupt");
        File.WriteAllBytes(path, new byte[1024]); // All zeros — invalid magic.

        Assert.Throws<InvalidDataException>(() => MutableBPlusTreeBytes.Open(path));
    }

    [Fact]
    public void Open_FailsOnTypedTreeMagic()
    {
        // A typed-tree file (PKBT magic) must not open as a bytes tree.
        // Distinct magic is the firewall against format mix-ups.
        string typedPath = Path.Combine(_tempDir, "typed.datum-pkindex");
        using (MutableBPlusTree typedTree = MutableBPlusTree.Create(typedPath, DatumIngest.Model.DataKind.Int32))
        {
            // Just create empty.
        }

        Assert.Throws<InvalidDataException>(() => MutableBPlusTreeBytes.Open(typedPath));
    }

    [Fact]
    public void TornHeader_FallsBackToPreviousSlot()
    {
        string path = PathFor("torn");

        using (MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path))
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] key = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, i);
                tree.Insert(new BytesIndexEntry(key, 0, i));
            }
        }

        long activeOffset = FindActiveSlotOffset(path);

        using (FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = activeOffset + 8; // Corrupt the commit-gen field — also kills the CRC.
            byte[] junk = new byte[8];
            new Random(0).NextBytes(junk);
            fs.Write(junk);
        }

        using MutableBPlusTreeBytes reopened = MutableBPlusTreeBytes.Open(path);
        Assert.True(reopened.EntryCount <= 20);

        // Must remain queryable.
        for (int i = 0; i < 20; i++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            _ = reopened.TryFind(key, out _);
        }
    }

    [Fact]
    public void BothSlotsCorrupt_ThrowsOnOpen()
    {
        string path = PathFor("doubly_torn");

        using (MutableBPlusTreeBytes _ = MutableBPlusTreeBytes.Create(path))
        {
            // Just create, then corrupt both slots.
        }

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize * 2];
            fs.Position = 0;
            fs.Write(zeros);
        }

        Assert.Throws<InvalidDataException>(() => MutableBPlusTreeBytes.Open(path));
    }

    [Fact]
    public void OneSlotCorrupt_OneValid_OpensSuccessfully()
    {
        string path = PathFor("one_torn");

        using (MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path))
        {
            tree.Insert(new BytesIndexEntry(new byte[] { 0x42 }, 0, 0));
        }

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
            fs.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            fs.Write(zeros);
        }

        using MutableBPlusTreeBytes reopened = MutableBPlusTreeBytes.Open(path);
        Assert.True(reopened.EntryCount == 0 || reopened.EntryCount == 1);
    }

    /// <summary>
    /// Reads both slots and returns the file offset of whichever has the higher
    /// (valid) commit generation — the slot a reader would pick.
    /// </summary>
    private static long FindActiveSlotOffset(string path)
    {
        byte[] slotA = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
        byte[] slotB = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);

        fs.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
        fs.ReadExactly(slotA);

        fs.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
        fs.ReadExactly(slotB);

        long genA = BinaryPrimitives.ReadInt64LittleEndian(slotA.AsSpan(8, 8));
        long genB = BinaryPrimitives.ReadInt64LittleEndian(slotB.AsSpan(8, 8));

        return genA >= genB
            ? MutableBPlusTreeConstants.HeaderSlotAOffset
            : MutableBPlusTreeConstants.HeaderSlotBOffset;
    }
}
