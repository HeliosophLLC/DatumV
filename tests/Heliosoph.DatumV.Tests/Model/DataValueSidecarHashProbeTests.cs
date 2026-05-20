using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Investigates whether sidecar-backed string values participate correctly in
/// <see cref="DataValue"/>'s hash + equality contract. Background: the sidecar
/// layout uses <c>(_p0, _p1)</c> for a 64-bit offset and <c>(_p2, low byte of _p3)</c>
/// for a 40-bit length — leaving no room for the cached content hash that
/// arena-backed strings keep in <c>(_p2, _p3)</c>. <see cref="DataValue.RawContentHash"/>
/// reads those same bytes unconditionally, so for sidecar values it returns
/// length-derived bits rather than an XxHash64 of the content.
///
/// These tests answer: does that latent design issue manifest as observable
/// wrong-answer behavior when sidecar strings flow through HashSet / Dictionary
/// keyed by DataValue (i.e. the path used by joins, group-by, distinct, IN, set ops)?
/// </summary>
public sealed class DataValueSidecarHashProbeTests
{
    [Fact]
    public void SidecarStrings_DistinctOffsetsSameLength_AreNotEqual()
    {
        // Two distinct sidecar references — same length, different absolute offsets.
        // If they compare equal, every join/group-by/distinct on a sidecar-backed
        // string column collapses values by length, which is a correctness bug.
        DataValue left = DataValue.FromStringInSidecar(offset: 1000, length: 42, storeId: 0);
        DataValue right = DataValue.FromStringInSidecar(offset: 9000, length: 42, storeId: 0);

        Assert.False(left.Equals(right),
            "Two sidecar-backed strings with the same length but different offsets should not be equal.");
    }

    [Fact]
    public void SidecarStrings_DistinctContent_DoNotCollideInHashSet()
    {
        // Probes the HashSet<DataValue> path that joins, distinct, group-by,
        // and set operations all rely on.
        HashSet<DataValue> set = new()
        {
            DataValue.FromStringInSidecar(offset: 100, length: 42, storeId: 0),
            DataValue.FromStringInSidecar(offset: 200, length: 42, storeId: 0),
            DataValue.FromStringInSidecar(offset: 300, length: 42, storeId: 0),
        };

        Assert.Equal(3, set.Count);
    }

    [Fact]
    public void SidecarStrings_HashCode_DiffersForDistinctOffsets()
    {
        // Weaker than the equality probe but worth recording: even if Equals
        // is "fixed" to compare offsets, GetHashCode driven by RawContentHash
        // returns length bits — which means HashSet probes would funnel every
        // same-length string into the same bucket.
        DataValue a = DataValue.FromStringInSidecar(offset: 100, length: 42, storeId: 0);
        DataValue b = DataValue.FromStringInSidecar(offset: 200, length: 42, storeId: 0);

        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SidecarStrings_ConstructedWithoutBytes_HaveNoCachedHash()
    {
        // Sidecar-backed values constructed from a (offset, length, storeId) triple
        // can't compute the hash — the bytes live on disk and the factory doesn't
        // own the IBlobSource needed to read them. The contract is "RawContentHash
        // returns 0 (no cached hash sentinel), so CompareStrings falls back to its
        // safe no-hash path." A future scan-time path can populate the cache when
        // it has both the bytes and the registry; this probe pins the present
        // contract so a regression to length-derived garbage gets caught.
        DataValue a = DataValue.FromStringInSidecar(offset: 100, length: 42, storeId: 0);
        DataValue b = DataValue.FromStringInSidecar(offset: 200, length: 42, storeId: 0);

        Assert.Equal(0UL, a.RawContentHash);
        Assert.Equal(0UL, b.RawContentHash);
    }
}
