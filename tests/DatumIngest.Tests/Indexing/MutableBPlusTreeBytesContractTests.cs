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
/// Every contract test runs against the new tree unchanged — if any
/// behavior diverges from <see cref="MutableBPlusTree"/>, the same test
/// that passes on the typed tree will fail here. That's the value of the
/// contract base.
/// </remarks>
public sealed class MutableBPlusTreeBytesContractTests : BPlusTreeContractTests
{
    protected override string FileExtension => ".datum-bytespkindex";

    protected override IMutableBPlusTreeAdapter CreateTree(string path, DataKind keyKind, bool allowDuplicates = false, int pageSize = 8192) =>
        new BytesAdapter(MutableBPlusTreeBytes.Create(path, allowDuplicates, pageSize));

    protected override IMutableBPlusTreeAdapter OpenTree(string path) =>
        new BytesAdapter(MutableBPlusTreeBytes.Open(path));

    /// <summary>
    /// Adapts <see cref="MutableBPlusTreeBytes"/> to the test-side
    /// <see cref="IMutableBPlusTreeAdapter"/> surface. Each DataValue key
    /// is single-encoded via <see cref="CompositeKeyEncoder.EncodeSingle"/>.
    /// Read results carry <c>default(DataValue)</c> in the Key slot — the
    /// tree only stores raw bytes and has no decoder. Contract tests assert
    /// on (ChunkIndex, RowOffsetInChunk) instead.
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

        public IReadOnlyList<ValueIndexEntry> FindAll(DataValue key)
        {
            byte[] encoded = CompositeKeyEncoder.EncodeSingle(key);
            IReadOnlyList<BytesIndexEntry> hits = _tree.FindAll(encoded);
            return ProjectToValueEntries(hits);
        }

        public IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high)
        {
            byte[] lowEncoded = CompositeKeyEncoder.EncodeSingle(low);
            byte[] highEncoded = CompositeKeyEncoder.EncodeSingle(high);
            IReadOnlyList<BytesIndexEntry> hits = _tree.FindRange(lowEncoded, highEncoded);
            return ProjectToValueEntries(hits);
        }

        public IEnumerable<ValueIndexEntry> TraverseForward()
        {
            foreach (BytesIndexEntry e in _tree.TraverseForward())
            {
                yield return new ValueIndexEntry(default, e.ChunkIndex, e.RowOffsetInChunk);
            }
        }

        public IEnumerable<ValueIndexEntry> TraverseBackward()
        {
            foreach (BytesIndexEntry e in _tree.TraverseBackward())
            {
                yield return new ValueIndexEntry(default, e.ChunkIndex, e.RowOffsetInChunk);
            }
        }

        public bool Delete(ValueIndexEntry entry)
        {
            byte[] encoded = CompositeKeyEncoder.EncodeSingle(entry.Key);
            return _tree.Delete(new BytesIndexEntry(encoded, entry.ChunkIndex, entry.RowOffsetInChunk));
        }

        public void Dispose() => _tree.Dispose();

        private static IReadOnlyList<ValueIndexEntry> ProjectToValueEntries(IReadOnlyList<BytesIndexEntry> hits)
        {
            if (hits.Count == 0) return Array.Empty<ValueIndexEntry>();
            ValueIndexEntry[] projected = new ValueIndexEntry[hits.Count];
            for (int i = 0; i < hits.Count; i++)
            {
                projected[i] = new ValueIndexEntry(default, hits[i].ChunkIndex, hits[i].RowOffsetInChunk);
            }
            return projected;
        }
    }
}
