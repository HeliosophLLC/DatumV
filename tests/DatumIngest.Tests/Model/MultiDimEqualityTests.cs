using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Equality and hashing must include the multi-dim shape, not just the
/// element data. Two arrays with the same flat elements but different
/// declared shapes must compare unequal — otherwise GROUP BY / HashSet /
/// distinct-row deduplication would silently merge shape-distinct values.
/// </summary>
public sealed class MultiDimEqualityTests : ServiceTestBase
{
    // ───────────────────── Inline path (16-byte payload) ─────────────────────

    [Fact]
    public void Inline_MultiDim_SameShapeSameElements_AreEqual()
    {
        // shape [2,2] = 8 bytes prefix + 4 × Int16 = 8 bytes elements = 16 (exact fit).
        DataValue a = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [2, 2], DataKind.Int16);
        DataValue b = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [2, 2], DataKind.Int16);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Inline_MultiDim_DifferentShape_AreNotEqual()
    {
        // Same flat content [1,2,3,4]; shape [2,2] vs [1,4] — must differ.
        DataValue twoByTwo = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [2, 2], DataKind.Int16);
        DataValue oneByFour = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [1, 4], DataKind.Int16);

        Assert.NotEqual(twoByTwo, oneByFour);
        // Hash inequality is high-probability but not guaranteed; we check the
        // value equality directly as the contract.
    }

    [Fact]
    public void Inline_MultiDim_DifferentElements_AreNotEqual()
    {
        // Same shape; different elements.
        DataValue a = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [2, 2], DataKind.Int16);
        DataValue b = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 5], [2, 2], DataKind.Int16);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Inline_MultiDim_VsFlat_AreNotEqual()
    {
        // Same 4 elements but one carries multi-dim flag + shape prefix, the
        // other is a flat 1-D array. The IsMultiDim flag is part of _flags,
        // which the IsArray equality arm compares — so they must differ.
        DataValue multi = DataValue.FromInlineMultiDimArray<short>([1, 2, 3, 4], [2, 2], DataKind.Int16);
        DataValue flat = DataValue.FromInlineArray<short>([1, 2, 3, 4], DataKind.Int16);

        Assert.NotEqual(multi, flat);
    }

    // ───────────────────── Arena path ─────────────────────

    [Fact]
    public void Arena_MultiDim_DifferentShape_AreNotEqual()
    {
        // Two arena-backed multi-dim values in the same arena, same elements,
        // different declared shapes. Different ndim packs into different
        // _charCount high-bytes, so equality must reject the match even
        // though _p0 (offset) and _p1 (length) might be the same.
        Arena arena = new();
        DataValue twoByThree = DataValue.FromArenaMultiDimArray<float>(
            [1f, 2f, 3f, 4f, 5f, 6f], [2, 3], DataKind.Float32, arena);
        DataValue threeByTwo = DataValue.FromArenaMultiDimArray<float>(
            [1f, 2f, 3f, 4f, 5f, 6f], [3, 2], DataKind.Float32, arena);

        Assert.NotEqual(twoByThree, threeByTwo);
    }

    [Fact]
    public void Arena_MultiDim_SameAllocation_AreEqual()
    {
        // Same arena, same offset, same shape — the canonical "identity"
        // case. Hash + equality must agree.
        Arena arena = new();
        DataValue a = DataValue.FromArenaMultiDimArray<float>(
            [1f, 2f, 3f, 4f, 5f, 6f], [2, 3], DataKind.Float32, arena);
        // Manually construct a second view with the same offset/length/shape.
        DataValue aliased = a;

        Assert.Equal(a, aliased);
        Assert.Equal(a.GetHashCode(), aliased.GetHashCode());
    }

    [Fact]
    public void Arena_MultiDim_VsFlat_SameElements_AreNotEqual()
    {
        // The IsMultiDim flag is part of _flags. Same elements, one flagged
        // multi-dim, one flat → unequal.
        Arena arena = new();
        DataValue multi = DataValue.FromArenaMultiDimArray<float>(
            [1f, 2f, 3f, 4f, 5f, 6f], [2, 3], DataKind.Float32, arena);
        DataValue flat = DataValue.FromArenaArray<float>(
            [1f, 2f, 3f, 4f, 5f, 6f], DataKind.Float32, arena);

        Assert.NotEqual(multi, flat);
    }

    // ───────────────────── HashCode stability ─────────────────────

    [Fact]
    public void Inline_MultiDim_HashCode_StableAcrossEqualValues()
    {
        // Equal values must hash equal — required for any hash-based container
        // (Dictionary, HashSet, GROUP BY).
        DataValue a = DataValue.FromInlineMultiDimArray<short>([10, 20, 30, 40], [2, 2], DataKind.Int16);
        DataValue b = DataValue.FromInlineMultiDimArray<short>([10, 20, 30, 40], [2, 2], DataKind.Int16);

        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // Sanity: a value with different content should (with high
        // probability) hash to a different bucket — but we don't assert this
        // strictly; only the equality-implies-hash-equality direction is
        // contractual.
    }
}
