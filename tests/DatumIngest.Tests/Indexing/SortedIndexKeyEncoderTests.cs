using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="SortedIndexKeyEncoder"/> verifying round-trip correctness,
/// sort-order preservation under byte comparison, and edge-case handling.
/// </summary>
public sealed class SortedIndexKeyEncoderTests
{
    [Theory]
    [InlineData(DataKind.Boolean, 1)]
    [InlineData(DataKind.UInt8, 1)]
    [InlineData(DataKind.Int8, 1)]
    [InlineData(DataKind.Int16, 2)]
    [InlineData(DataKind.UInt16, 2)]
    [InlineData(DataKind.Int32, 4)]
    [InlineData(DataKind.UInt32, 4)]
    [InlineData(DataKind.Float32, 4)]
    [InlineData(DataKind.Date, 4)]
    [InlineData(DataKind.Int64, 8)]
    [InlineData(DataKind.UInt64, 8)]
    [InlineData(DataKind.Float64, 8)]
    [InlineData(DataKind.DateTime, 8)]
    [InlineData(DataKind.Time, 8)]
    [InlineData(DataKind.Duration, 8)]
    [InlineData(DataKind.String, 8)]
    [InlineData(DataKind.Uuid, 16)]
    public void GetKeyWidth_ReturnsExpectedWidth(DataKind kind, int expectedWidth)
    {
        Assert.Equal(expectedWidth, SortedIndexKeyEncoder.GetKeyWidth(kind));
    }

    [Fact]
    public void GetKeyWidth_UnsupportedKind_Throws()
    {
        Assert.Throws<NotSupportedException>(() => SortedIndexKeyEncoder.GetKeyWidth(DataKind.Vector));
    }

    // ──────────────── Round-trip tests ────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_Boolean(bool value)
    {
        DataValue original = DataValue.FromBoolean(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(original.AsBoolean(), decoded.AsBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(255)]
    public void RoundTrip_UInt8(byte value)
    {
        DataValue original = DataValue.FromUInt8(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsUInt8());
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public void RoundTrip_Int8(sbyte value)
    {
        DataValue original = DataValue.FromInt8(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsInt8());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32767)]
    [InlineData(65535)]
    public void RoundTrip_UInt16(ushort value)
    {
        DataValue original = DataValue.FromUInt16(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsUInt16());
    }

    [Theory]
    [InlineData(-32768)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32767)]
    public void RoundTrip_Int16(short value)
    {
        DataValue original = DataValue.FromInt16(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsInt16());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2147483647u)]
    [InlineData(4294967295u)]
    public void RoundTrip_UInt32(uint value)
    {
        DataValue original = DataValue.FromUInt32(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsUInt32());
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void RoundTrip_Int32(int value)
    {
        DataValue original = DataValue.FromInt32(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsInt32());
    }

    [Theory]
    [InlineData(0uL)]
    [InlineData(1uL)]
    [InlineData(ulong.MaxValue)]
    public void RoundTrip_UInt64(ulong value)
    {
        DataValue original = DataValue.FromUInt64(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsUInt64());
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void RoundTrip_Int64(long value)
    {
        DataValue original = DataValue.FromInt64(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsInt64());
    }

    [Theory]
    [InlineData(-1.5f)]
    [InlineData(0.0f)]
    [InlineData(1.5f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity)]
    public void RoundTrip_Float32(float value)
    {
        DataValue original = DataValue.FromFloat32(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsFloat32());
    }

    [Theory]
    [InlineData(-1.5)]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    public void RoundTrip_Float64(double value)
    {
        DataValue original = DataValue.FromFloat64(value);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(value, decoded.AsFloat64());
    }

    [Fact]
    public void RoundTrip_Float32_NaN()
    {
        DataValue original = DataValue.FromFloat32(float.NaN);
        DataValue decoded = EncodeThenDecode(original);
        Assert.True(float.IsNaN(decoded.AsFloat32()));
    }

    [Fact]
    public void RoundTrip_Float64_NaN()
    {
        DataValue original = DataValue.FromFloat64(double.NaN);
        DataValue decoded = EncodeThenDecode(original);
        Assert.True(double.IsNaN(decoded.AsFloat64()));
    }

    [Fact]
    public void RoundTrip_Date()
    {
        DateOnly date = new(2026, 4, 7);
        DataValue original = DataValue.FromDate(date);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(date, decoded.AsDate());
    }

    [Fact]
    public void RoundTrip_DateTime()
    {
        DateTimeOffset dateTime = new(2026, 4, 7, 14, 30, 0, TimeSpan.FromHours(2));
        DataValue original = DataValue.FromDateTime(dateTime);
        DataValue decoded = EncodeThenDecode(original);

        // UTC instant is preserved; original offset is lost (decoded as UTC).
        Assert.Equal(dateTime.UtcTicks, decoded.AsDateTime().UtcTicks);
    }

    [Fact]
    public void RoundTrip_Time()
    {
        TimeOnly time = new(14, 30, 45);
        DataValue original = DataValue.FromTime(time);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(time, decoded.AsTime());
    }

    [Fact]
    public void RoundTrip_Duration()
    {
        TimeSpan duration = TimeSpan.FromHours(-3.5);
        DataValue original = DataValue.FromDuration(duration);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(duration, decoded.AsDuration());
    }

    [Fact]
    public void RoundTrip_Uuid()
    {
        Guid uuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        DataValue original = DataValue.FromUuid(uuid);
        DataValue decoded = EncodeThenDecode(original);
        Assert.Equal(uuid, decoded.AsUuid());
    }

    [Fact]
    public void RoundTrip_StringReference()
    {
        byte[] buffer = new byte[8];
        SortedIndexKeyEncoder.EncodeStringReference(1234, 567, buffer);
        (int offset, int length) = SortedIndexKeyEncoder.DecodeStringReference(buffer);

        Assert.Equal(1234, offset);
        Assert.Equal(567, length);
    }

    // ──────────────── Sort-order preservation tests ────────────────

    [Fact]
    public void SortOrder_Int32_PreservedByByteComparison()
    {
        int[] values = [int.MinValue, -1000, -1, 0, 1, 1000, int.MaxValue];
        AssertSortOrderPreserved(values, DataValue.FromInt32);
    }

    [Fact]
    public void SortOrder_Int8_PreservedByByteComparison()
    {
        sbyte[] values = [-128, -1, 0, 1, 127];
        AssertSortOrderPreserved(values, DataValue.FromInt8);
    }

    [Fact]
    public void SortOrder_Int16_PreservedByByteComparison()
    {
        short[] values = [short.MinValue, -1, 0, 1, short.MaxValue];
        AssertSortOrderPreserved(values, DataValue.FromInt16);
    }

    [Fact]
    public void SortOrder_Int64_PreservedByByteComparison()
    {
        long[] values = [long.MinValue, -1L, 0L, 1L, long.MaxValue];
        AssertSortOrderPreserved(values, DataValue.FromInt64);
    }

    [Fact]
    public void SortOrder_UInt8_PreservedByByteComparison()
    {
        byte[] values = [0, 1, 127, 128, 255];
        AssertSortOrderPreserved(values, DataValue.FromUInt8);
    }

    [Fact]
    public void SortOrder_UInt16_PreservedByByteComparison()
    {
        ushort[] values = [0, 1, 32767, 32768, 65535];
        AssertSortOrderPreserved(values, DataValue.FromUInt16);
    }

    [Fact]
    public void SortOrder_UInt32_PreservedByByteComparison()
    {
        uint[] values = [0u, 1u, 2147483647u, 2147483648u, 4294967295u];
        AssertSortOrderPreserved(values, DataValue.FromUInt32);
    }

    [Fact]
    public void SortOrder_UInt64_PreservedByByteComparison()
    {
        ulong[] values = [0uL, 1uL, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue];
        AssertSortOrderPreserved(values, DataValue.FromUInt64);
    }

    [Fact]
    public void SortOrder_Float32_PreservedByByteComparison()
    {
        float[] values = [float.NegativeInfinity, -1.5f, -float.Epsilon, 0.0f, float.Epsilon, 1.5f, float.PositiveInfinity];
        AssertSortOrderPreserved(values, DataValue.FromFloat32);
    }

    [Fact]
    public void SortOrder_Float64_PreservedByByteComparison()
    {
        double[] values = [double.NegativeInfinity, -1.5, -double.Epsilon, 0.0, double.Epsilon, 1.5, double.PositiveInfinity];
        AssertSortOrderPreserved(values, DataValue.FromFloat64);
    }

    [Fact]
    public void SortOrder_Date_PreservedByByteComparison()
    {
        DateOnly[] values = [new(1, 1, 1), new(2000, 1, 1), new(2026, 4, 7), new(9999, 12, 31)];
        AssertSortOrderPreserved(values, DataValue.FromDate);
    }

    [Fact]
    public void SortOrder_DateTime_PreservedByByteComparison()
    {
        DataValue[] keys =
        [
            DataValue.FromDateTime(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero)),
            DataValue.FromDateTime(new DateTimeOffset(2026, 4, 7, 14, 30, 0, TimeSpan.Zero)),
        ];
        AssertSortOrderPreservedRaw(keys);
    }

    [Fact]
    public void SortOrder_DateTime_DifferentOffsets_PreservedByUtcTicks()
    {
        // Same UTC instant expressed with different offsets should encode identically.
        DataValue utc = DataValue.FromDateTime(new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero));
        DataValue eastern = DataValue.FromDateTime(new DateTimeOffset(2026, 4, 7, 8, 0, 0, TimeSpan.FromHours(-4)));

        byte[] utcBytes = new byte[8];
        byte[] easternBytes = new byte[8];
        SortedIndexKeyEncoder.Encode(utc, utcBytes);
        SortedIndexKeyEncoder.Encode(eastern, easternBytes);

        Assert.Equal(utcBytes, easternBytes);
    }

    [Fact]
    public void SortOrder_Time_PreservedByByteComparison()
    {
        TimeOnly[] values = [new(0, 0, 0), new(6, 30, 0), new(12, 0, 0), new(23, 59, 59)];
        AssertSortOrderPreserved(values, DataValue.FromTime);
    }

    [Fact]
    public void SortOrder_Duration_PreservedByByteComparison()
    {
        TimeSpan[] values = [TimeSpan.FromHours(-24), TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromDays(365)];
        AssertSortOrderPreserved(values, DataValue.FromDuration);
    }

    [Fact]
    public void SortOrder_Boolean_FalseBeforeTrue()
    {
        byte[] falseBytes = new byte[1];
        byte[] trueBytes = new byte[1];
        SortedIndexKeyEncoder.Encode(DataValue.FromBoolean(false), falseBytes);
        SortedIndexKeyEncoder.Encode(DataValue.FromBoolean(true), trueBytes);

        Assert.True(falseBytes.AsSpan().SequenceCompareTo(trueBytes) < 0);
    }

    // ──────────────── Edge cases ────────────────

    [Fact]
    public void Encode_Float32_NegativeZero_EqualsPositiveZero()
    {
        byte[] positiveZeroBytes = new byte[4];
        byte[] negativeZeroBytes = new byte[4];

        SortedIndexKeyEncoder.Encode(DataValue.FromFloat32(0.0f), positiveZeroBytes);
        SortedIndexKeyEncoder.Encode(DataValue.FromFloat32(-0.0f), negativeZeroBytes);

        Assert.Equal(positiveZeroBytes, negativeZeroBytes);
    }

    [Fact]
    public void Encode_Float64_NegativeZero_EqualsPositiveZero()
    {
        byte[] positiveZeroBytes = new byte[8];
        byte[] negativeZeroBytes = new byte[8];

        SortedIndexKeyEncoder.Encode(DataValue.FromFloat64(0.0), positiveZeroBytes);
        SortedIndexKeyEncoder.Encode(DataValue.FromFloat64(-0.0), negativeZeroBytes);

        Assert.Equal(positiveZeroBytes, negativeZeroBytes);
    }

    [Fact]
    public void Encode_Float32_AllNaN_EncodeIdentically()
    {
        byte[] canonicalBytes = new byte[4];
        byte[] customNanBytes = new byte[4];

        SortedIndexKeyEncoder.Encode(DataValue.FromFloat32(float.NaN), canonicalBytes);

        // Construct a non-canonical NaN by bit manipulation.
        float customNan = BitConverter.Int32BitsToSingle(0x7FC00001);
        SortedIndexKeyEncoder.Encode(DataValue.FromFloat32(customNan), customNanBytes);

        Assert.Equal(canonicalBytes, customNanBytes);
    }

    [Fact]
    public void Encode_Float64_AllNaN_EncodeIdentically()
    {
        byte[] canonicalBytes = new byte[8];
        byte[] customNanBytes = new byte[8];

        SortedIndexKeyEncoder.Encode(DataValue.FromFloat64(double.NaN), canonicalBytes);

        double customNan = BitConverter.Int64BitsToDouble(0x7FF8000000000001L);
        SortedIndexKeyEncoder.Encode(DataValue.FromFloat64(customNan), customNanBytes);

        Assert.Equal(canonicalBytes, customNanBytes);
    }

    [Fact]
    public void Encode_StringKey_Throws()
    {
        DataValue stringValue = DataValue.FromString("hello");
        byte[] buffer = new byte[8];
        Assert.Throws<InvalidOperationException>(() => SortedIndexKeyEncoder.Encode(stringValue, buffer));
    }

    [Fact]
    public void Decode_StringKind_Throws()
    {
        byte[] buffer = new byte[8];
        Assert.Throws<InvalidOperationException>(() => SortedIndexKeyEncoder.Decode(DataKind.String, buffer));
    }

    [Fact]
    public void Encode_UnsupportedKind_Throws()
    {
        DataValue vector = DataValue.FromVector([1.0f, 2.0f]);
        byte[] buffer = new byte[16];
        Assert.Throws<NotSupportedException>(() => SortedIndexKeyEncoder.Encode(vector, buffer));
    }

    // ──────────────── Helpers ────────────────

    /// <summary>
    /// Encodes a <see cref="DataValue"/>, then decodes it and returns the result.
    /// </summary>
    private static DataValue EncodeThenDecode(DataValue value)
    {
        int width = SortedIndexKeyEncoder.GetKeyWidth(value.Kind);
        byte[] buffer = new byte[width];
        SortedIndexKeyEncoder.Encode(value, buffer);
        return SortedIndexKeyEncoder.Decode(value.Kind, buffer);
    }

    /// <summary>
    /// Encodes a sequence of values (assumed to be in ascending order) and asserts that
    /// the encoded byte sequences maintain strictly ascending order under
    /// <see cref="MemoryExtensions.SequenceCompareTo{T}"/>.
    /// </summary>
    private static void AssertSortOrderPreserved<T>(T[] values, Func<T, DataValue> factory)
    {
        DataValue[] keys = Array.ConvertAll(values, value => factory(value));
        AssertSortOrderPreservedRaw(keys);
    }

    /// <summary>
    /// Encodes a sequence of <see cref="DataValue"/> keys (assumed to be in ascending order)
    /// and asserts that encoded byte sequences maintain strictly ascending order.
    /// </summary>
    private static void AssertSortOrderPreservedRaw(DataValue[] keys)
    {
        int width = SortedIndexKeyEncoder.GetKeyWidth(keys[0].Kind);
        byte[][] encoded = new byte[keys.Length][];

        for (int index = 0; index < keys.Length; index++)
        {
            encoded[index] = new byte[width];
            SortedIndexKeyEncoder.Encode(keys[index], encoded[index]);
        }

        for (int index = 1; index < encoded.Length; index++)
        {
            int comparison = encoded[index - 1].AsSpan().SequenceCompareTo(encoded[index]);
            Assert.True(
                comparison < 0,
                $"Expected encoded[{index - 1}] < encoded[{index}] but got comparison={comparison}");
        }
    }
}
