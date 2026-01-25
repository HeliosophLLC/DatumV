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
/// </remarks>
public interface IMutableBPlusTreeAdapter : IDisposable
{
    long EntryCount { get; }
    int TreeHeight { get; }
    uint PageCount { get; }
    bool AllowDuplicates { get; }

    void Insert(ValueIndexEntry entry);
    bool TryFind(DataValue key, out ValueIndexEntry entry);
}
