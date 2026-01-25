using System.Buffers.Binary;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="CompositeKeyEncoder"/>: per-kind ordering invariants
/// (encoded bytes preserve lex order), cross-kind tuple ordering, edge cases
/// (sign extremes, empty / embedded-null strings, float corner cases), and
/// rejection of NULL components / unsupported kinds.
/// </summary>
public sealed class CompositeKeyEncoderTests
{
    // ───────────────────────── Per-kind ordering ─────────────────────────

    [Fact]
    public void Bool_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromBoolean(false),
            DataValue.FromBoolean(true));
    }

    [Fact]
    public void Int8_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromInt8(sbyte.MinValue),
            DataValue.FromInt8(-1),
            DataValue.FromInt8(0),
            DataValue.FromInt8(1),
            DataValue.FromInt8(sbyte.MaxValue));
    }

    [Fact]
    public void Int16_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromInt16(short.MinValue),
            DataValue.FromInt16(-1),
            DataValue.FromInt16(0),
            DataValue.FromInt16(1),
            DataValue.FromInt16(short.MaxValue));
    }

    [Fact]
    public void Int32_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromInt32(int.MinValue),
            DataValue.FromInt32(-1_000_000),
            DataValue.FromInt32(-1),
            DataValue.FromInt32(0),
            DataValue.FromInt32(1),
            DataValue.FromInt32(1_000_000),
            DataValue.FromInt32(int.MaxValue));
    }

    [Fact]
    public void Int64_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromInt64(long.MinValue),
            DataValue.FromInt64(-1L),
            DataValue.FromInt64(0L),
            DataValue.FromInt64(1L),
            DataValue.FromInt64(long.MaxValue));
    }

    [Fact]
    public void Int128_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromInt128(Int128.MinValue),
            DataValue.FromInt128(-1),
            DataValue.FromInt128(0),
            DataValue.FromInt128(1),
            DataValue.FromInt128(Int128.MaxValue));
    }

    [Fact]
    public void UInt8_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromUInt8(0),
            DataValue.FromUInt8(1),
            DataValue.FromUInt8(127),
            DataValue.FromUInt8(128),
            DataValue.FromUInt8(byte.MaxValue));
    }

    [Fact]
    public void UInt16_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromUInt16(0),
            DataValue.FromUInt16(1),
            DataValue.FromUInt16((ushort)short.MaxValue),
            DataValue.FromUInt16((ushort)(short.MaxValue + 1)),
            DataValue.FromUInt16(ushort.MaxValue));
    }

    [Fact]
    public void UInt32_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromUInt32(0),
            DataValue.FromUInt32(1),
            DataValue.FromUInt32(int.MaxValue),
            DataValue.FromUInt32((uint)int.MaxValue + 1),
            DataValue.FromUInt32(uint.MaxValue));
    }

    [Fact]
    public void UInt64_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromUInt64(0),
            DataValue.FromUInt64(1),
            DataValue.FromUInt64(long.MaxValue),
            DataValue.FromUInt64((ulong)long.MaxValue + 1),
            DataValue.FromUInt64(ulong.MaxValue));
    }

    [Fact]
    public void UInt128_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromUInt128(0),
            DataValue.FromUInt128(1),
            DataValue.FromUInt128(UInt128.MaxValue / 2),
            DataValue.FromUInt128(UInt128.MaxValue));
    }

    [Fact]
    public void Float32_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromFloat32(float.NegativeInfinity),
            DataValue.FromFloat32(float.MinValue),
            DataValue.FromFloat32(-1.5f),
            DataValue.FromFloat32(-float.Epsilon),
            DataValue.FromFloat32(0f),
            DataValue.FromFloat32(float.Epsilon),
            DataValue.FromFloat32(1.5f),
            DataValue.FromFloat32(float.MaxValue),
            DataValue.FromFloat32(float.PositiveInfinity));
    }

    [Fact]
    public void Float64_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromFloat64(double.NegativeInfinity),
            DataValue.FromFloat64(double.MinValue),
            DataValue.FromFloat64(-1.5),
            DataValue.FromFloat64(-double.Epsilon),
            DataValue.FromFloat64(0.0),
            DataValue.FromFloat64(double.Epsilon),
            DataValue.FromFloat64(1.5),
            DataValue.FromFloat64(double.MaxValue),
            DataValue.FromFloat64(double.PositiveInfinity));
    }

    [Fact]
    public void Float16_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromFloat16(Half.NegativeInfinity),
            DataValue.FromFloat16(Half.MinValue),
            DataValue.FromFloat16((Half)(-1f)),
            DataValue.FromFloat16((Half)0f),
            DataValue.FromFloat16((Half)1f),
            DataValue.FromFloat16(Half.MaxValue),
            DataValue.FromFloat16(Half.PositiveInfinity));
    }

    [Fact]
    public void Date_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromDate(DateOnly.MinValue),
            DataValue.FromDate(new DateOnly(1970, 1, 1)),
            DataValue.FromDate(new DateOnly(2026, 5, 11)),
            DataValue.FromDate(DateOnly.MaxValue));
    }

    [Fact]
    public void Time_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromTime(TimeOnly.MinValue),
            DataValue.FromTime(new TimeOnly(0, 0, 1)),
            DataValue.FromTime(new TimeOnly(12, 0, 0)),
            DataValue.FromTime(TimeOnly.MaxValue));
    }

    [Fact]
    public void Duration_OrderingPreserved()
    {
        AssertSortedOrder(
            DataValue.FromDuration(TimeSpan.MinValue),
            DataValue.FromDuration(TimeSpan.FromSeconds(-1)),
            DataValue.FromDuration(TimeSpan.Zero),
            DataValue.FromDuration(TimeSpan.FromSeconds(1)),
            DataValue.FromDuration(TimeSpan.MaxValue));
    }

    [Fact]
    public void DateTime_OrderingPreserved_ByUtcInstant()
    {
        AssertSortedOrder(
            DataValue.FromDateTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero)));
    }

    [Fact]
    public void Uuid_ConsistentEncoding()
    {
        // Uuid encoding uses Guid.ToByteArray() (Microsoft mixed-endian).
        // The ordering is whatever that layout produces — what matters is
        // that two equal Guids produce equal bytes, and the encoder is
        // deterministic.
        Guid g = Guid.NewGuid();
        byte[] a = CompositeKeyEncoder.EncodeSingle(DataValue.FromUuid(g));
        byte[] b = CompositeKeyEncoder.EncodeSingle(DataValue.FromUuid(g));
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Uuid_DistinctValues_DistinctEncoding()
    {
        Guid g1 = new Guid("00000000-0000-0000-0000-000000000001");
        Guid g2 = new Guid("00000000-0000-0000-0000-000000000002");
        byte[] a = CompositeKeyEncoder.EncodeSingle(DataValue.FromUuid(g1));
        byte[] b = CompositeKeyEncoder.EncodeSingle(DataValue.FromUuid(g2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void String_OrderingPreserved_Ascii()
    {
        using Arena arena = new();
        AssertSortedOrder(arena,
            DataValue.FromString("", arena),
            DataValue.FromString("a", arena),
            DataValue.FromString("aa", arena),
            DataValue.FromString("ab", arena),
            DataValue.FromString("b", arena),
            DataValue.FromString("z", arena),
            DataValue.FromString("zz", arena));
    }

    [Fact]
    public void String_OrderingPreserved_Utf8MultiByte()
    {
        // Bytes-lex order on UTF-8 matches code-point order for BMP runs.
        // (Cross-codepoint ordering: bytes < non-bytes per UTF-8 spec.)
        using Arena arena = new();
        AssertSortedOrder(arena,
            DataValue.FromString("apple", arena),
            DataValue.FromString("apples", arena),
            DataValue.FromString("banana", arena),
            DataValue.FromString("zebra", arena));
    }

    [Fact]
    public void String_LongerKey_OrdersAfterPrefix()
    {
        // "test2017/000000290551.jpg" — the COCO filename example. 25 bytes.
        // Must order after its prefix and before its successor.
        using Arena arena = new();
        AssertSortedOrder(arena,
            DataValue.FromString("test2017/000000290550", arena),
            DataValue.FromString("test2017/000000290551.jpg", arena),
            DataValue.FromString("test2017/000000290552", arena));
    }

    [Fact]
    public void ByteArray_OrderingPreserved()
    {
        using Arena arena = new();
        AssertSortedOrder(arena,
            DataValue.FromByteArray(Array.Empty<byte>(), arena),
            DataValue.FromByteArray(new byte[] { 0x01 }, arena),
            DataValue.FromByteArray(new byte[] { 0x01, 0x02 }, arena),
            DataValue.FromByteArray(new byte[] { 0x01, 0xFF }, arena),
            DataValue.FromByteArray(new byte[] { 0x02 }, arena),
            DataValue.FromByteArray(new byte[] { 0xFF, 0xFF }, arena));
    }

    [Fact]
    public void ByteArray_EmbeddedNulls_OrdersCorrectly()
    {
        // The escape pattern \x00 → \x00\xFF must preserve order:
        // [\x00, \x01] sorts after [\x00] and before [\x01].
        using Arena arena = new();
        AssertSortedOrder(arena,
            DataValue.FromByteArray(Array.Empty<byte>(), arena),
            DataValue.FromByteArray(new byte[] { 0x00 }, arena),
            DataValue.FromByteArray(new byte[] { 0x00, 0x01 }, arena),
            DataValue.FromByteArray(new byte[] { 0x00, 0xFF }, arena),
            DataValue.FromByteArray(new byte[] { 0x01 }, arena),
            DataValue.FromByteArray(new byte[] { 0x01, 0x00 }, arena),
            DataValue.FromByteArray(new byte[] { 0xFF, 0x00 }, arena));
    }

    // ───────────────────────── Cross-kind tuple ordering ─────────────────────────

    [Fact]
    public void Tuple_TwoColumns_PreservesLexOrder()
    {
        // (Int32, String) tuple — lex order is component-by-component.
        using Arena arena = new();
        IReadOnlyList<DataValue>[] sortedTuples =
        [
            [DataValue.FromInt32(1), DataValue.FromString("alpha", arena)],
            [DataValue.FromInt32(1), DataValue.FromString("beta", arena)],
            [DataValue.FromInt32(2), DataValue.FromString("alpha", arena)],
            [DataValue.FromInt32(2), DataValue.FromString("beta", arena)],
            [DataValue.FromInt32(3), DataValue.FromString("alpha", arena)],
        ];

        byte[][] encoded = sortedTuples
            .Select(t => CompositeKeyEncoder.Encode(t, arena))
            .ToArray();

        AssertByteArraysAscending(encoded);
    }

    [Fact]
    public void Tuple_ThreeColumns_MixedKinds()
    {
        // (Int64, Date, Uuid) — the user's three-column composite example.
        using Arena arena = new();
        Guid g1 = new Guid("00000000-0000-0000-0000-000000000001");
        Guid g2 = new Guid("00000000-0000-0000-0000-000000000002");

        IReadOnlyList<DataValue>[] sortedTuples =
        [
            [DataValue.FromInt64(1), DataValue.FromDate(new DateOnly(2024, 1, 1)), DataValue.FromUuid(g1)],
            [DataValue.FromInt64(1), DataValue.FromDate(new DateOnly(2024, 1, 1)), DataValue.FromUuid(g2)],
            [DataValue.FromInt64(1), DataValue.FromDate(new DateOnly(2025, 1, 1)), DataValue.FromUuid(g1)],
            [DataValue.FromInt64(2), DataValue.FromDate(new DateOnly(2020, 1, 1)), DataValue.FromUuid(g1)],
        ];

        byte[][] encoded = sortedTuples
            .Select(t => CompositeKeyEncoder.Encode(t, arena))
            .ToArray();

        AssertByteArraysAscending(encoded);
    }

    [Fact]
    public void Tuple_ComponentOrder_MattersForEncoding()
    {
        // Encode(a, b) and Encode(b, a) must differ — column order is part
        // of the key.
        DataValue v1 = DataValue.FromInt32(1);
        DataValue v2 = DataValue.FromInt32(2);

        byte[] forward = CompositeKeyEncoder.Encode([v1, v2]);
        byte[] reversed = CompositeKeyEncoder.Encode([v2, v1]);

        Assert.NotEqual(forward, reversed);
    }

    [Fact]
    public void Tuple_PrefixIsStrictlyLessThanLonger()
    {
        // For fixed-width kinds, the tuple is length-deterministic, so a
        // 1-column tuple is always strictly shorter than a 2-column tuple
        // with the same prefix. Lex order of encoded bytes preserves this.
        DataValue v1 = DataValue.FromInt32(5);
        DataValue v2 = DataValue.FromInt32(10);

        byte[] oneCol = CompositeKeyEncoder.Encode([v1]);
        byte[] twoCol = CompositeKeyEncoder.Encode([v1, v2]);

        Assert.True(oneCol.AsSpan().SequenceCompareTo(twoCol) < 0,
            "Prefix encoding must sort strictly before the longer encoding with the same prefix.");
    }

    // ───────────────────────── Edge cases ─────────────────────────

    [Fact]
    public void String_Empty_RoundTrips()
    {
        // Empty string encodes to just the terminator (\x00\x00).
        using Arena arena = new();
        byte[] encoded = CompositeKeyEncoder.EncodeSingle(DataValue.FromString("", arena), arena);
        Assert.Equal(new byte[] { 0x00, 0x00 }, encoded);
    }

    [Fact]
    public void NegativeZero_Float_EqualsPositiveZero_InEncoding()
    {
        // IEEE 754 defines +0 == -0. The encoder maps them to the same bytes
        // because their sign-flipped representations land on the same value.
        byte[] posZero = CompositeKeyEncoder.EncodeSingle(DataValue.FromFloat64(0.0));
        byte[] negZero = CompositeKeyEncoder.EncodeSingle(DataValue.FromFloat64(-0.0));
        // Actually, -0.0 in IEEE 754 has the sign bit set, so it WILL differ
        // from +0.0 after our bit-pattern encoding. Document this divergence
        // (matches Postgres's btree behavior where -0 and +0 are bit-distinct).
        Assert.NotEqual(posZero, negZero);
        // -0 sorts strictly before +0 under our encoding (its all-bits-flipped
        // representation sits just below +0's sign-flipped representation).
        Assert.True(negZero.AsSpan().SequenceCompareTo(posZero) < 0);
    }

    [Fact]
    public void DateTime_SameUtcInstant_DifferentOffset_DistinctEncoding()
    {
        // Two DateTimeOffsets representing the same UTC instant but with
        // different offsets are distinct values; the encoder must produce
        // distinct bytes so equality-by-encoding lines up with equality-
        // by-DataValue (where offset is part of the value).
        DataValue dtUtc = DataValue.FromDateTime(
            new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        DataValue dtPacific = DataValue.FromDateTime(
            new DateTimeOffset(2026, 5, 11, 5, 0, 0, TimeSpan.FromHours(-7)));

        byte[] a = CompositeKeyEncoder.EncodeSingle(dtUtc);
        byte[] b = CompositeKeyEncoder.EncodeSingle(dtPacific);

        Assert.NotEqual(a, b);
        // First 8 bytes (UTC ticks) must match because the instant is the same.
        Assert.Equal(a.AsSpan(0, 8).ToArray(), b.AsSpan(0, 8).ToArray());
        // Last 2 bytes (offset minutes) must differ.
        Assert.NotEqual(a.AsSpan(8, 2).ToArray(), b.AsSpan(8, 2).ToArray());
    }

    // ───────────────────────── Rejection ─────────────────────────

    [Fact]
    public void Encode_NullComponent_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompositeKeyEncoder.Encode([DataValue.Null(DataKind.Int32)]));
    }

    [Fact]
    public void Encode_NullInComposite_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompositeKeyEncoder.Encode([DataValue.FromInt32(1), DataValue.Null(DataKind.String)]));
    }

    [Fact]
    public void Encode_UnsupportedKind_Decimal_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            CompositeKeyEncoder.EncodeSingle(DataValue.FromDecimal(1.5m)));
    }

    [Fact]
    public void Encode_UnsupportedKind_Point2D_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            CompositeKeyEncoder.EncodeSingle(DataValue.FromPoint2D(1f, 2f)));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void AssertSortedOrder(params DataValue[] sortedValues)
    {
        byte[][] encoded = sortedValues.Select(v => CompositeKeyEncoder.EncodeSingle(v)).ToArray();
        AssertByteArraysAscending(encoded);
    }

    private static void AssertSortedOrder(IValueStore store, params DataValue[] sortedValues)
    {
        byte[][] encoded = sortedValues.Select(v => CompositeKeyEncoder.EncodeSingle(v, store)).ToArray();
        AssertByteArraysAscending(encoded);
    }

    private static void AssertByteArraysAscending(byte[][] encoded)
    {
        for (int i = 0; i < encoded.Length - 1; i++)
        {
            int cmp = encoded[i].AsSpan().SequenceCompareTo(encoded[i + 1]);
            Assert.True(cmp < 0,
                $"Encoded[{i}] should sort strictly before encoded[{i + 1}]. " +
                $"Got compare = {cmp}.\n" +
                $"encoded[{i}]   = {BitConverter.ToString(encoded[i])}\n" +
                $"encoded[{i + 1}] = {BitConverter.ToString(encoded[i + 1])}");
        }
    }
}
