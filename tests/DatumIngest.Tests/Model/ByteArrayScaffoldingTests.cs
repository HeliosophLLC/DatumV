using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip tests for the byte-array migration scaffolding. PR1 of the migration
/// has shipped: the new <c>FromByteArray*</c> factories now produce
/// <see cref="DataKind.UInt8Array"/> values with the <c>IsArray</c> flag set —
/// the migration's intermediate state. Switches on <c>UInt8Array</c> still match,
/// flag-based dispatch also works, and the final cutover PR will flip the kind
/// to <see cref="DataKind.UInt8"/>.
/// </summary>
public sealed class ByteArrayScaffoldingTests : ServiceTestBase
{
    [Fact]
    public void FromByteArray_ProducesUInt8ArrayKindWithIsArrayFlag()
    {
        Arena arena = new();
        byte[] payload = [10, 20, 30, 40, 50];

        DataValue value = DataValue.FromByteArray(payload, arena);

        // Intermediate state: kind stays UInt8Array so existing switches keep
        // matching, but the IsArray flag is now set on top — that's the bridge to
        // the final cutover where the kind moves to UInt8.
        Assert.Equal(DataKind.UInt8Array, value.Kind);
        Assert.True(value.IsArray);
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
    public void FromByteArray_NewAndLegacyForms_ProduceEquivalentByteContent()
    {
        // Both factories produce values that read back as identical bytes through
        // AsByteSpan. Kinds also match in this intermediate stage (both UInt8Array);
        // the only observable difference is the IsArray flag bit on the new form.
        Arena arena = new();
        byte[] payload = [11, 22, 33, 44, 55, 66, 77, 88, 99];

        DataValue legacyForm = DataValue.FromUInt8Array(payload, arena);
        DataValue newForm = DataValue.FromByteArray(payload, arena);

        Assert.Equal(legacyForm.AsByteSpan(arena).ToArray(),
                     newForm.AsByteSpan(arena).ToArray());
        Assert.Equal(legacyForm.AsUInt8Array(arena),
                     newForm.AsUInt8Array(arena));

        // Same kind during migration; the only difference is the flag.
        Assert.Equal(legacyForm.Kind, newForm.Kind);
        Assert.Equal(DataKind.UInt8Array, newForm.Kind);
        Assert.True(legacyForm.IsArray);
        Assert.True(newForm.IsArray);
    }

    [Fact]
    public void FromByteArrayAtOffset_RoundTripsBytesFromArena()
    {
        Arena arena = new();
        byte[] payload = [101, 102, 103, 104];
        var (offset, length) = arena.StoreBytes(payload);

        DataValue value = DataValue.FromByteArrayAtOffset(offset, length);

        Assert.Equal(DataKind.UInt8Array, value.Kind);
        Assert.True(value.IsArray);
        Assert.Equal(payload, value.AsUInt8Array(arena));
    }

    [Fact]
    public void FromByteArrayInSidecar_BuildsSidecarFlaggedValue()
    {
        // Pure metadata-only test — no actual sidecar I/O. Verifies the kind+flag+coords
        // packing is correct so that a later read against a real SidecarReadStore would
        // resolve through the same path as legacy FromUInt8ArrayInSidecar.
        const long Offset = 32;
        const long Length = 1024;
        const byte StoreId = 0;

        DataValue value = DataValue.FromByteArrayInSidecar(Offset, Length, StoreId);

        Assert.Equal(DataKind.UInt8Array, value.Kind);
        Assert.True(value.IsInSidecar);
        Assert.True(value.IsArray);
        Assert.False(value.IsNull);
    }

    [Fact]
    public void FromByteArrayInSidecar_AndLegacy_HaveEquivalentSidecarCoordinates()
    {
        // Both factories pack the same offset / length / storeId into the same bit
        // positions. In the intermediate stage the kinds also match — only the
        // IsArray flag distinguishes the new factory's output.
        const long Offset = 1024;
        const long Length = 4096;
        const byte StoreId = 7;

        DataValue legacy = DataValue.FromUInt8ArrayInSidecar(Offset, Length, StoreId);
        DataValue updated = DataValue.FromByteArrayInSidecar(Offset, Length, StoreId);

        Assert.True(legacy.IsInSidecar);
        Assert.True(updated.IsInSidecar);
        Assert.True(legacy.IsArray);
        Assert.True(updated.IsArray);
        Assert.Equal(legacy.Kind, updated.Kind);
        Assert.Equal(DataKind.UInt8Array, updated.Kind);
    }

    [Fact]
    public void AsByteSpan_AcceptsNewModelValue()
    {
        // Regression guard: the accessor accepts both legacy (UInt8Array kind) and
        // new-model (UInt8 + IsArray flag) values. In the migration's intermediate
        // stage the new factory still emits UInt8Array kind, so this exercises the
        // legacy-kind path; the widened guard means a future kind flip won't break it.
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
        // A scalar UInt8 value (no IsArray flag) must still be rejected — only the
        // array form should pass the accessor's kind check.
        DataValue scalar = DataValue.FromUInt8(99);

        Assert.False(scalar.IsArray);
        Assert.Throws<InvalidOperationException>(() => scalar.AsByteSpan(new Arena()));
    }
}
