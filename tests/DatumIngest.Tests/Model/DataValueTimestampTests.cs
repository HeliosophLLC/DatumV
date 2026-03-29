using System.Runtime.CompilerServices;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Slice-1 contract for the PG-faithful Timestamp / TimestampTz split that
/// replaces the .NET-DateTimeOffset-shaped <c>DataKind.DateTime</c>. These
/// tests are RED until slice 1 lands the new API; they spell out the
/// behaviour the implementation must satisfy.
///
/// Two kinds, both 8 bytes:
///   • <c>Timestamp</c>   — naive wall-clock ticks, no tz info (PG `timestamp`)
///   • <c>TimestampTz</c> — UTC ticks, no per-row offset       (PG `timestamptz`)
///
/// The old <c>DataKind.DateTime</c> (12 bytes inline, ticks + offset minutes)
/// goes away. <c>_p2</c> is no longer used by either temporal kind.
/// </summary>
public sealed class DataValueTimestampTests : ServiceTestBase
{
    private Arena RentArena() => GetService<Pool>().Backing.RentArena();
    private void ReturnArena(Arena arena) => GetService<Pool>().Backing.TryReturn(arena);

    // ─── TimestampTz: UTC normalisation ──────────────────────────────────

    [Fact]
    public void FromTimestampTz_NonUtcInput_NormalisesToUtc()
    {
        // PG `timestamptz`: input offset is parsed, value converts to UTC,
        // original offset is forgotten. Same instant, but the carried offset
        // is always +00:00 on readback.
        DateTimeOffset input = new(2026, 5, 19, 12, 0, 0, TimeSpan.FromHours(-7));
        DataValue value = DataValue.FromTimestampTz(input);

        Assert.Equal(DataKind.TimestampTz, value.Kind);
        DateTimeOffset readback = value.AsTimestampTz();
        Assert.Equal(input.UtcDateTime, readback.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, readback.Offset);
    }

    [Fact]
    public void FromTimestampTz_AlreadyUtcInput_RoundTrips()
    {
        DateTimeOffset input = new(2026, 5, 19, 19, 0, 0, TimeSpan.Zero);
        DataValue value = DataValue.FromTimestampTz(input);

        DateTimeOffset readback = value.AsTimestampTz();
        Assert.Equal(input, readback);
        Assert.Equal(TimeSpan.Zero, readback.Offset);
    }

    [Fact]
    public void TimestampTz_SameInstantWithDifferentInputOffsets_AreEqual()
    {
        // PG: '2026-05-19T12:00:00-07:00'::timestamptz =
        //     '2026-05-19T19:00:00+00:00'::timestamptz
        // The previous DateTimeOffset-shaped DataKind.DateTime treated these
        // as not-equal because the stored offsets differed. PG-faithful
        // TimestampTz must coalesce them.
        DateTimeOffset western = new(2026, 5, 19, 12, 0, 0, TimeSpan.FromHours(-7));
        DateTimeOffset utc     = new(2026, 5, 19, 19, 0, 0, TimeSpan.Zero);
        DataValue a = DataValue.FromTimestampTz(western);
        DataValue b = DataValue.FromTimestampTz(utc);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TimestampTz_DoesNotUseOffsetPayloadSlot()
    {
        // _p2 used to carry offset minutes for DataKind.DateTime. Under the
        // PG-faithful split it must be unused; two values built from inputs
        // with different non-UTC offsets but the same instant must be
        // bit-identical, not merely Equals-equal.
        DateTimeOffset western = new(2026, 5, 19, 12, 0, 0, TimeSpan.FromHours(-7));
        DateTimeOffset eastern = new(2026, 5, 19, 22, 0, 0, TimeSpan.FromHours(3));
        DataValue a = DataValue.FromTimestampTz(western); // 19:00 UTC
        DataValue b = DataValue.FromTimestampTz(eastern); // 19:00 UTC

        ReadOnlySpan<byte> ba = AsBytes(a);
        ReadOnlySpan<byte> bb = AsBytes(b);
        Assert.True(ba.SequenceEqual(bb),
            "TimestampTz values for the same UTC instant must be byte-identical; "
            + "the previous _p2 offset slot is gone.");
    }

    // ─── Timestamp: naive semantics ──────────────────────────────────────

    [Fact]
    public void FromTimestamp_StoresNaiveTicks()
    {
        DateTime naive = new(2026, 5, 19, 12, 0, 0, DateTimeKind.Unspecified);
        DataValue value = DataValue.FromTimestamp(naive);

        Assert.Equal(DataKind.Timestamp, value.Kind);
        DateTime readback = value.AsTimestamp();
        Assert.Equal(naive.Ticks, readback.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, readback.Kind);
    }

    [Fact]
    public void Timestamp_AndTimestampTz_AreDistinctKinds()
    {
        // Same Int64 ticks, different kinds → different values. No implicit
        // cross-kind equality; explicit cast required (slice 4).
        DateTime naive = new(2026, 5, 19, 12, 0, 0, DateTimeKind.Unspecified);
        DateTimeOffset utc = new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);

        DataValue a = DataValue.FromTimestamp(naive);
        DataValue b = DataValue.FromTimestampTz(utc);

        Assert.NotEqual(a.Kind, b.Kind);
        Assert.NotEqual(a, b);
    }

    // ─── Scalar byte size (array-element width) ──────────────────────────

    [Fact]
    public void ScalarByteSize_BothTemporalKinds_AreEight()
    {
        // Both kinds carry 8-byte ticks. The previous DateTime-as-12-bytes
        // wart that blocked Array<DateTime> is gone.
        Assert.Equal(8, InvokeScalarByteSize(DataKind.Timestamp));
        Assert.Equal(8, InvokeScalarByteSize(DataKind.TimestampTz));
    }

    // ─── Array<Timestamp> / Array<TimestampTz> round-trip ────────────────

    [Fact]
    public void FromPrimitiveArray_TimestampTz_RoundTripsLongArray()
    {
        // Previously threw NotSupportedException ("Array<DateTime> is not
        // supported via FromPrimitiveArray"). Under the PG-faithful split
        // this is a trivial Int64-ticks round-trip.
        long[] ticks =
        [
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks,
            new DateTimeOffset(2026, 5, 19, 19, 0, 0, TimeSpan.Zero).UtcTicks,
            new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero).UtcTicks,
        ];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(ticks, DataKind.TimestampTz);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.TimestampTz, dv.Kind);
            Assert.True(dv.IsArray);
            ReadOnlySpan<long> readback = dv.AsArraySpan<long>(arena);
            Assert.Equal(ticks.Length, readback.Length);
            for (int i = 0; i < ticks.Length; i++)
            {
                Assert.Equal(ticks[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    [Fact]
    public void FromPrimitiveArray_Timestamp_RoundTripsLongArray()
    {
        long[] ticks =
        [
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).Ticks,
            new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Unspecified).Ticks,
            new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Unspecified).Ticks,
        ];
        ValueRef arrayRef = ValueRef.FromPrimitiveArray(ticks, DataKind.Timestamp);

        Arena arena = RentArena();
        try
        {
            DataValue dv = arrayRef.ToDataValue(arena);
            Assert.Equal(DataKind.Timestamp, dv.Kind);
            Assert.True(dv.IsArray);
            ReadOnlySpan<long> readback = dv.AsArraySpan<long>(arena);
            Assert.Equal(ticks.Length, readback.Length);
            for (int i = 0; i < ticks.Length; i++)
            {
                Assert.Equal(ticks[i], readback[i]);
            }
        }
        finally { ReturnArena(arena); }
    }

    // ─── PG temporal arithmetic ───────────────────────────────────────────

    [Fact]
    public void PromoteArithmeticKind_TimestampTzPlusDuration_ReturnsTimestampTz()
    {
        Assert.Equal(
            DataKind.TimestampTz,
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.TimestampTz, DataKind.Duration,
                DatumIngest.Parsing.Ast.BinaryOperator.Add));
    }

    [Fact]
    public void PromoteArithmeticKind_TimestampTzMinusTimestampTz_ReturnsDuration()
    {
        Assert.Equal(
            DataKind.Duration,
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.TimestampTz, DataKind.TimestampTz,
                DatumIngest.Parsing.Ast.BinaryOperator.Subtract));
    }

    [Fact]
    public void PromoteArithmeticKind_TimestampPlusDuration_ReturnsTimestamp()
    {
        Assert.Equal(
            DataKind.Timestamp,
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.Timestamp, DataKind.Duration,
                DatumIngest.Parsing.Ast.BinaryOperator.Add));
    }

    [Fact]
    public void PromoteArithmeticKind_TimestampMinusTimestamp_ReturnsDuration()
    {
        Assert.Equal(
            DataKind.Duration,
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.Timestamp, DataKind.Timestamp,
                DatumIngest.Parsing.Ast.BinaryOperator.Subtract));
    }

    [Fact]
    public void PromoteArithmeticKind_DurationPlusTimestampTz_IsCommutative()
    {
        // Add is commutative — Duration + TimestampTz also resolves to
        // TimestampTz so `now() + interval` and `interval + now()` both work.
        Assert.Equal(
            DataKind.TimestampTz,
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.Duration, DataKind.TimestampTz,
                DatumIngest.Parsing.Ast.BinaryOperator.Add));
    }

    [Fact]
    public void PromoteArithmeticKind_TimestampMinusTimestampTz_Throws()
    {
        // Cross-kind subtraction isn't a temporal pair. The strict promotion
        // table rejects it — callers must explicitly cast one side to match
        // the other (slice 4 cast matrix).
        Assert.Throws<InvalidOperationException>(() =>
            DatumIngest.Execution.ExpressionEvaluator.PromoteArithmeticKind(
                DataKind.Timestamp, DataKind.TimestampTz,
                DatumIngest.Parsing.Ast.BinaryOperator.Subtract));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static int InvokeScalarByteSize(DataKind kind)
    {
        // ScalarByteSize is internal; tests run against the same assembly
        // via InternalsVisibleTo for DatumIngest.Tests.
        return DataValue.ScalarByteSize(kind);
    }

    private static ReadOnlySpan<byte> AsBytes(in DataValue value)
    {
        // Reinterpret the 32-byte struct as a byte span for bit-identity
        // assertions.
        ref byte head = ref Unsafe.As<DataValue, byte>(ref Unsafe.AsRef(in value));
        return System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            ref head, DataValue.SizeBytes);
    }
}
