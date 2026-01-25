using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Indexing.BTree.MutableBytes;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Runs the shared <see cref="BPlusTreeContractTests"/> suite against
/// <see cref="MutableBPlusTreeBytes"/>, the bytes-keyed B+Tree
/// implementation. The DataValue keys produced by the contract base get
/// encoded through <see cref="CompositeKeyEncoder.EncodeSingle"/> in the
/// adapter — the same encoder production composite-PK callers will use.
/// </summary>
/// <remarks>
/// Every contract test (14 of them) runs against the new tree
/// unchanged — if any behavior diverges from <see cref="MutableBPlusTree"/>,
/// the same test that passes on the typed tree will fail here. That's
/// the value of the contract base.
/// </remarks>
public sealed class MutableBPlusTreeBytesContractTests : BPlusTreeContractTests
{
    protected override string FileExtension => ".datum-bytespkindex";

    protected override IMutableBPlusTreeAdapter CreateTree(string path, DataKind keyKind) =>
        new BytesAdapter(MutableBPlusTreeBytes.Create(path));

    protected override IMutableBPlusTreeAdapter OpenTree(string path) =>
        new BytesAdapter(MutableBPlusTreeBytes.Open(path));

    /// <summary>
    /// Adapts <see cref="MutableBPlusTreeBytes"/> to the test-side
    /// <see cref="IMutableBPlusTreeAdapter"/> surface. Each DataValue key
    /// is single-encoded via <see cref="CompositeKeyEncoder.EncodeSingle"/>;
    /// the original DataValue is replayed back to the caller on TryFind
    /// because the contract tests assert on the DataValue shape.
    /// </summary>
    private sealed class BytesAdapter : IMutableBPlusTreeAdapter
    {
        private readonly MutableBPlusTreeBytes _tree;

        internal BytesAdapter(MutableBPlusTreeBytes tree)
        {
            _tree = tree;
        }

        public long EntryCount => _tree.EntryCount;
        public int TreeHeight => _tree.TreeHeight;
        public uint PageCount => _tree.PageCount;
        public bool AllowDuplicates => _tree.AllowDuplicates;

        public void Insert(ValueIndexEntry entry)
        {
            byte[] encoded = CompositeKeyEncoder.EncodeSingle(entry.Key);
            try
            {
                _tree.Insert(new BytesIndexEntry(encoded, entry.ChunkIndex, entry.RowOffsetInChunk));
            }
            catch (DuplicateKeyException)
            {
                // Map the bytes-tree exception to the typed-tree exception the
                // contract tests expect.
                throw new DuplicatePrimaryKeyException(entry.Key);
            }
        }

        public bool TryFind(DataValue key, out ValueIndexEntry entry)
        {
            byte[] encoded = CompositeKeyEncoder.EncodeSingle(key);
            if (_tree.TryFind(encoded, out BytesIndexEntry hit))
            {
                entry = new ValueIndexEntry(key, hit.ChunkIndex, hit.RowOffsetInChunk);
                return true;
            }
            entry = default;
            return false;
        }

        public void Dispose() => _tree.Dispose();
    }
}
