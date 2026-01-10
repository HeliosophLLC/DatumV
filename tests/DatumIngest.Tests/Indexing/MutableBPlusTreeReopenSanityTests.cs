using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Regression tests verifying create → insert → dispose → reopen semantics
/// for <see cref="MutableBPlusTree"/>. Catches accidental file-handle leaks
/// in lifecycle code that wraps the tree (PR10h's PK-index sidecar).
/// </summary>
public sealed class MutableBPlusTreeReopenSanityTests : IDisposable
{
    private readonly string _tempDir;

    public MutableBPlusTreeReopenSanityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MutableBTreeReopen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Create_Insert_Dispose_Reopen_Works()
    {
        string path = Path.Combine(_tempDir, "t.datum-pkindex");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(1), 0, 0));
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(2), 0, 0));
        } // tree disposed — file should be released

        // Re-open should succeed; FileStream's Dispose released the handle.
        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);
        Assert.Equal(2, reopened.EntryCount);
    }
}
