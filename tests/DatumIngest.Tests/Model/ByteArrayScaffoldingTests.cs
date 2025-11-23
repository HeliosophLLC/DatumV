using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip tests for byte-array DataValues. After the UInt8Array migration,
/// byte arrays use <see cref="DataKind.UInt8"/> with the <c>IsArray</c> flag set
/// at both the DataValue and schema layers (<see cref="ColumnInfo.IsArray"/>).
/// </summary>
public sealed class ByteArrayScaffoldingTests : ServiceTestBase
{
    [Fact]
    public void FromByteArray_ProducesUInt8KindWithIsArrayFlag()
    {
        Arena arena = new();
        byte[] payload = [10, 20, 30, 40, 50];

        DataValue value = DataValue.FromByteArray(payload, arena);

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsByteArrayKind);
        Assert.False(value.IsNull);
        Assert.False(value.IsInSidecar);
        Assert.False(value.IsInlineArray);
    }

    [Fact]
    public void FromByteArray_RoundTripsThroughAsByteSpan()
    {
        Arena arena = new();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF];

        DataValue value = DataValue.FromByteArray(payload, arena);
        ReadOnlySpan<byte> recovered = value.AsByteSpan(arena);

        Assert.Equal(payload, recovered.ToArray());
    }

    [Fact]
    public void FromByteArray_RoundTripsThroughAsUInt8Array()
    {
        Arena arena = new();
        byte[] payload = [1, 2, 3, 4];

        DataValue value = DataValue.FromByteArray(payload, arena);
        byte[] recovered = value.AsUInt8Array(arena);

        Assert.Equal(payload, recovered);
    }

    [Fact]
    public void FromByteArrayAtOffset_RoundTripsBytesFromArena()
    {
        Arena arena = new();
        byte[] payload = [101, 102, 103, 104];
        var (offset, length) = arena.StoreBytes(payload);

        DataValue value = DataValue.FromByteArrayAtOffset(offset, length);

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.True(value.IsArray);
        Assert.True(value.IsByteArrayKind);
        Assert.Equal(payload, value.AsUInt8Array(arena));
    }

    [Fact]
    public void FromByteArrayInSidecar_BuildsSidecarFlaggedValue()
    {
        // Pure metadata-only test — no actual sidecar I/O. Verifies the kind+flag+coords
        // packing is correct so that a later read against a real SidecarReadStore would
        // resolve through the byte-array path.
        const long Offset = 32;
        const long Length = 1024;
        const byte StoreId = 0;

        DataValue value = DataValue.FromByteArrayInSidecar(Offset, Length, StoreId);

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.True(value.IsInSidecar);
        Assert.True(value.IsArray);
        Assert.True(value.IsByteArrayKind);
        Assert.False(value.IsNull);
    }

    [Fact]
    public void AsByteSpan_AcceptsByteArrayValue()
    {
        Arena arena = new();
        byte[] payload = [42];

        DataValue value = DataValue.FromByteArray(payload, arena);
        ReadOnlySpan<byte> bytes = value.AsByteSpan(arena);

        Assert.Equal(1, bytes.Length);
        Assert.Equal((byte)42, bytes[0]);
    }

    [Fact]
    public void AsByteSpan_RejectsScalarUInt8WithoutIsArrayFlag()
    {
        // A scalar UInt8 value (no IsArray flag) must be rejected — only the
        // array form passes the accessor's kind check.
        DataValue scalar = DataValue.FromUInt8(99);

        Assert.False(scalar.IsArray);
        Assert.False(scalar.IsByteArrayKind);
        Assert.Throws<InvalidOperationException>(() => scalar.AsByteSpan(new Arena()));
    }

    [Fact]
    public void NullByteArray_HasUInt8KindWithIsArrayFlag()
    {
        DataValue n = DataValue.NullByteArray();
        Assert.Equal(DataKind.UInt8, n.Kind);
        Assert.True(n.IsArray);
        Assert.True(n.IsNull);
    }
}
