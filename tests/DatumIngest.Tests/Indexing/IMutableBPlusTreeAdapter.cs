using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Common contract every mutable B+Tree implementation must satisfy. The
/// shared <see cref="BPlusTreeContractTests"/> base exercises this surface
/// so the same correctness invariants apply across the typed tree
/// (DataValue-keyed) and the bytes-keyed tree variants.
/// </summary>
/// <remarks>
/// The interface intentionally uses <see cref="ValueIndexEntry"/> /
/// <see cref="DataValue"/> as the public key shape — bytes-keyed
/// implementations expose an adapter that single-encodes the DataValue
/// to bytes internally. That keeps the contract tests value-shape
/// agnostic and lets a single test suite cover both implementations.
/// Read-side results from the bytes tree carry <c>default(DataValue)</c>
/// in the <see cref="ValueIndexEntry.Key"/> slot because raw bytes can't
/// be decoded back without a per-kind decoder; contract tests assert on
/// <see cref="ValueIndexEntry.ChunkIndex"/> /
/// <see cref="ValueIndexEntry.RowOffsetInChunk"/> instead.
/// </remarks>
public interface IMutableBPlusTreeAdapter : IDisposable
{
    long EntryCount { get; }
    int TreeHeight { get; }
    uint PageCount { get; }
    bool AllowDuplicates { get; }

    void Insert(ValueIndexEntry entry);
    bool TryFind(DataValue key, out ValueIndexEntry entry);

    IReadOnlyList<ValueIndexEntry> FindAll(DataValue key);
    IReadOnlyList<ValueIndexEntry> FindRange(DataValue low, DataValue high);
    IEnumerable<ValueIndexEntry> TraverseForward();
    IEnumerable<ValueIndexEntry> TraverseBackward();
    bool Delete(ValueIndexEntry entry);
}
