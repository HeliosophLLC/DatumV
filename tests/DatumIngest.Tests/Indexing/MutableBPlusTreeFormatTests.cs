using System.Buffers.Binary;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// File-format-specific tests for the typed <see cref="MutableBPlusTree"/>:
/// torn-write recovery, corrupted-header detection, dual-slot fallback.
/// These exercise the concrete on-disk layout (CRC slots, slot-A/B offsets,
/// commit-gen) and do not generalize across tree implementations — each
/// implementation gets its own format-tests sibling file.
/// </summary>
public sealed class MutableBPlusTreeFormatTests : IDisposable
{
    private readonly string _tempDir;

    public MutableBPlusTreeFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MutableBTreeFormat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name + ".datum-pkindex");

    [Fact]
    public void KeyKind_PersistsAcrossReopen()
    {
        string path = PathFor("kind");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            Assert.Equal(DataKind.Int32, tree.KeyKind);
        }

        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);
        Assert.Equal(DataKind.Int32, reopened.KeyKind);
    }

    [Fact]
    public void Open_FailsOnCorruptFile()
    {
        string path = PathFor("corrupt");
        File.WriteAllBytes(path, new byte[1024]); // All zeros — invalid magic.

        Assert.Throws<InvalidDataException>(() => MutableBPlusTree.Open(path));
    }

    [Fact]
    public void TornHeader_FallsBackToPreviousSlot()
    {
        string path = PathFor("torn");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            for (int i = 0; i < 20; i++)
            {
                tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        // Corrupt the active slot — whichever has the higher commit gen.
        // The reader picks the surviving slot by gen + CRC.
        long activeOffset = FindActiveSlotOffset(path);

        using (FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = activeOffset + 8; // Corrupt the commit-gen field — also kills the CRC.
            byte[] junk = new byte[8];
            new Random(0).NextBytes(junk);
            fs.Write(junk);
        }

        // Reopen — should fall back to the older slot.
        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);

        // The previous-but-one commit might be the empty Create-time state, or any
        // intermediate INSERT commit. Either way, the tree must open cleanly.
        Assert.True(reopened.EntryCount <= 20);

        // And it must remain queryable. We can't predict which exact commit gen
        // recovered, but every find should either succeed or fail without crashing.
        for (int i = 0; i < 20; i++)
        {
            _ = reopened.TryFind(DataValue.FromInt32(i), out _);
        }
    }

    [Fact]
    public void BothSlotsCorrupt_ThrowsOnOpen()
    {
        string path = PathFor("doubly_torn");

        using (MutableBPlusTree _ = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            // Just create, then corrupt both slots.
        }

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize * 2];
            fs.Position = 0;
            fs.Write(zeros);
        }

        Assert.Throws<InvalidDataException>(() => MutableBPlusTree.Open(path));
    }

    [Fact]
    public void OneSlotCorrupt_OneValid_OpensSuccessfully()
    {
        string path = PathFor("one_torn");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(99), 0, 0));
        }

        // Zero out slot A only.
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
            fs.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            fs.Write(zeros);
        }

        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);

        // Whichever surviving slot is active, we either get the empty state (if
        // the only valid slot was Create's) or the post-insert state. Either way,
        // the file opens.
        Assert.True(reopened.EntryCount == 0 || reopened.EntryCount == 1);
    }

    /// <summary>
    /// Reads both slots and returns the file offset of whichever has the higher
    /// (valid) commit generation — i.e. the one a reader would pick. Used by
    /// torn-write tests to target the active slot for corruption.
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
