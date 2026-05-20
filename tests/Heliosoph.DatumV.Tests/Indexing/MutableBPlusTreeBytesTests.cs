using Heliosoph.DatumV.Indexing.BTree.MutableBytes;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Bytes-keyed tree behavior tests covering what the typed tree can't do —
/// long keys (the COCO filename motivation), embedded nulls in keys, mixed
/// key lengths within a single tree, and direct byte[] insert/lookup
/// without going through DataValue.
/// </summary>
public sealed class MutableBPlusTreeBytesTests : IDisposable
{
    private readonly string _tempDir;

    public MutableBPlusTreeBytesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MutableBytesTree_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name + ".datum-bytespkindex");

    // ───── COCO filename: 25-char keys that the typed tree's 16-byte cap rejects ─────

    [Fact]
    public void Insert_LongKeys_RoundTrip()
    {
        string path = PathFor("coco");
        using MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path);

        // The COCO2017 filename example from the user — 25 ASCII bytes per key.
        for (int i = 0; i < 100; i++)
        {
            string filename = $"test2017/{i:D12}.jpg";
            byte[] key = System.Text.Encoding.ASCII.GetBytes(filename);
            tree.Insert(new BytesIndexEntry(key, ChunkIndex: i / 10, RowOffsetInChunk: i));
        }

        Assert.Equal(100, tree.EntryCount);

        for (int i = 0; i < 100; i++)
        {
            string filename = $"test2017/{i:D12}.jpg";
            byte[] key = System.Text.Encoding.ASCII.GetBytes(filename);
            Assert.True(tree.TryFind(key, out BytesIndexEntry found),
                $"Long-key entry for {filename} missing");
            Assert.Equal(i / 10, found.ChunkIndex);
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    [Fact]
    public void Insert_VeryLongKey_Single()
    {
        string path = PathFor("verylong");
        using MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path);

        // 512-byte key — far past anything the typed tree could handle.
        byte[] key = new byte[512];
        new Random(42).NextBytes(key);

        tree.Insert(new BytesIndexEntry(key, 0, 0));

        Assert.True(tree.TryFind(key, out BytesIndexEntry found));
        Assert.Equal(0, found.ChunkIndex);
        Assert.Equal(0, found.RowOffsetInChunk);
    }

    // ───── Embedded nulls in keys ─────

    [Fact]
    public void Insert_KeysWithEmbeddedNulls_OrderedCorrectly()
    {
        string path = PathFor("nulls");
        using MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path);

        // These keys all contain \x00 mid-payload. The encoder doesn't
        // care — keys are opaque bytes. Verify order is preserved.
        byte[][] keys =
        [
            new byte[] { 0x00 },
            new byte[] { 0x00, 0x00 },
            new byte[] { 0x00, 0x01 },
            new byte[] { 0x00, 0xFF },
            new byte[] { 0x01 },
            new byte[] { 0x01, 0x00 },
        ];

        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert(new BytesIndexEntry(keys[i], 0, i));
        }

        Assert.Equal(keys.Length, tree.EntryCount);

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(tree.TryFind(keys[i], out BytesIndexEntry found));
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    // ───── Mixed-length keys in one tree ─────

    [Fact]
    public void Insert_MixedKeyLengths_AllFound()
    {
        string path = PathFor("mixed");
        using MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path);

        byte[][] keys =
        [
            new byte[] { 0x01 },
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[100],   // long key
            new byte[] { 0xFF },
        ];
        // Make the long key non-trivial so it doesn't clash with the [0x01...] prefix sequence.
        keys[3][0] = 0x42;

        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert(new BytesIndexEntry(keys[i], 0, i));
        }

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(tree.TryFind(keys[i], out BytesIndexEntry found));
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    // ───── Persistence with long keys ─────

    [Fact]
    public void LongKey_PersistsAcrossReopen()
    {
        string path = PathFor("persist_long");
        byte[] key = System.Text.Encoding.ASCII.GetBytes("test2017/000000290551.jpg");

        using (MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path))
        {
            tree.Insert(new BytesIndexEntry(key, ChunkIndex: 7, RowOffsetInChunk: 42L));
        }

        using MutableBPlusTreeBytes reopened = MutableBPlusTreeBytes.Open(path);
        Assert.Equal(1, reopened.EntryCount);
        Assert.True(reopened.TryFind(key, out BytesIndexEntry found));
        Assert.Equal(7, found.ChunkIndex);
        Assert.Equal(42L, found.RowOffsetInChunk);
    }

    // ───── Multi-level split with long keys ─────

    [Fact]
    public void Insert_LongKeys_ForcesMultiLevelSplits()
    {
        string path = PathFor("split_long");
        using MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path);

        // 100-byte keys → each leaf entry is ~116 bytes → ~70 entries fit
        // per leaf → 5000 keys forces at least height 3.
        const int Total = 5000;
        for (int i = 0; i < Total; i++)
        {
            byte[] key = new byte[100];
            BitConverter.TryWriteBytes(key, i);  // first 4 bytes = i (little-endian)
            // Ensure unique-and-sortable: use big-endian-ish prefix
            key[0] = (byte)((i >> 24) & 0xFF);
            key[1] = (byte)((i >> 16) & 0xFF);
            key[2] = (byte)((i >> 8) & 0xFF);
            key[3] = (byte)(i & 0xFF);

            tree.Insert(new BytesIndexEntry(key, ChunkIndex: i / 1000, RowOffsetInChunk: i));
        }

        Assert.Equal(Total, tree.EntryCount);
        Assert.True(tree.TreeHeight >= 2,
            $"Expected at least one level of internal split (height ≥ 2) after {Total} long-key entries; height = {tree.TreeHeight}");

        // Sample lookups.
        int[] samples = { 0, 1, 999, 2500, Total - 1 };
        foreach (int i in samples)
        {
            byte[] key = new byte[100];
            key[0] = (byte)((i >> 24) & 0xFF);
            key[1] = (byte)((i >> 16) & 0xFF);
            key[2] = (byte)((i >> 8) & 0xFF);
            key[3] = (byte)(i & 0xFF);
            Assert.True(tree.TryFind(key, out BytesIndexEntry found), $"Key {i} missing");
            Assert.Equal(i / 1000, found.ChunkIndex);
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }
}
