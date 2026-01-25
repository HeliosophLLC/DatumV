using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Runs the shared <see cref="BPlusTreeContractTests"/> suite against the
/// typed <see cref="MutableBPlusTree"/> implementation (DataValue-keyed
/// pages). Any new B+Tree implementation gets a sibling subclass that
/// runs the same suite — the same correctness contract applies regardless
/// of key shape.
/// </summary>
public sealed class MutableBPlusTreeContractTests : BPlusTreeContractTests
{
    protected override string FileExtension => ".datum-pkindex";

    protected override IMutableBPlusTreeAdapter CreateTree(string path, DataKind keyKind) =>
        new TypedAdapter(MutableBPlusTree.Create(path, keyKind));

    protected override IMutableBPlusTreeAdapter OpenTree(string path) =>
        new TypedAdapter(MutableBPlusTree.Open(path));

    /// <summary>
    /// Adapts <see cref="MutableBPlusTree"/> (whose accessors are
    /// <c>internal</c>) to the test-side <see cref="IMutableBPlusTreeAdapter"/>
    /// surface. Pure pass-through — no behavior changes vs. calling the
    /// tree directly.
    /// </summary>
    private sealed class TypedAdapter : IMutableBPlusTreeAdapter
    {
        private readonly MutableBPlusTree _tree;

        internal TypedAdapter(MutableBPlusTree tree)
        {
            _tree = tree;
        }

        public long EntryCount => _tree.EntryCount;
        public int TreeHeight => _tree.TreeHeight;
        public uint PageCount => _tree.PageCount;
        public bool AllowDuplicates => _tree.AllowDuplicates;

        public void Insert(ValueIndexEntry entry) => _tree.Insert(entry);

        public bool TryFind(DataValue key, out ValueIndexEntry entry) =>
            _tree.TryFind(key, out entry);

        public void Dispose() => _tree.Dispose();
    }
}
