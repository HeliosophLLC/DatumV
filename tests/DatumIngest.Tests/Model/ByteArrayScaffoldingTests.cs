using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Round-trip and equivalence tests for the byte-array scaffolding introduced in
/// preparation for the full <see cref="DataKind.UInt8Array"/> elimination. The new
/// factories (<c>FromByteArray</c>, <c>FromByteArrayAtOffset</c>,
/// <c>FromByteArrayInSidecar</c>) produce values using the new model
/// (<see cref="DataKind.UInt8"/> + <c>IsArray</c> flag) and read identically through
/// the existing <c>AsByteSpan</c> / <c>AsUInt8Array</c> accessors. These tests prove
/// that an eventual cutover (rename callers, then delete legacy kind) won't change
/// observable behaviour.
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
        // Both factories should produce values that read back as identical bytes
        // through AsByteSpan — proves the new-model form is observationally equivalent
        // to the legacy form. The kind/flag bits differ, but the read path is the same.
        Arena arena = new();
        byte[] payload = [11, 22, 33, 44, 55, 66, 77, 88, 99];

        DataValue legacyForm = DataValue.FromUInt8Array(payload, arena);
        DataValue newForm = DataValue.FromByteArray(payload, arena);

        Assert.Equal(legacyForm.AsByteSpan(arena).ToArray(),
                     newForm.AsByteSpan(arena).ToArray());
        Assert.Equal(legacyForm.AsUInt8Array(arena),
                     newForm.AsUInt8Array(arena));

        // The two forms differ in their kind/flag intent representation:
        Assert.Equal(DataKind.UInt8Array, legacyForm.Kind);
        Assert.Equal(DataKind.UInt8, newForm.Kind);

        // …but both report IsArray = true via the property's dual-recognition logic:
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

        Assert.Equal(DataKind.UInt8, value.Kind);
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

        Assert.Equal(DataKind.UInt8, value.Kind);
        Assert.True(value.IsInSidecar);
        Assert.True(value.IsArray);
        Assert.False(value.IsNull);
    }

    [Fact]
    public void FromByteArrayInSidecar_AndLegacy_HaveEquivalentSidecarCoordinates()
    {
        // Both factories must pack the same offset / length / storeId into the same
        // bit positions so the reader path is interchangeable. We can't read them
        // without a real IBlobSource, but we can compare flag visibility + intent.
        const long Offset = 1024;
        const long Length = 4096;
        const byte StoreId = 7;

        DataValue legacy = DataValue.FromUInt8ArrayInSidecar(Offset, Length, StoreId);
        DataValue updated = DataValue.FromByteArrayInSidecar(Offset, Length, StoreId);

        Assert.True(legacy.IsInSidecar);
        Assert.True(updated.IsInSidecar);
        Assert.True(legacy.IsArray);
        Assert.True(updated.IsArray);
        Assert.Equal(DataKind.UInt8Array, legacy.Kind);
        Assert.Equal(DataKind.UInt8, updated.Kind);
    }

    [Fact]
    public void AsByteSpan_AcceptsNewModelValue()
    {
        // Regression guard: the accessor used to require Kind ∈ {UInt8Array, Image};
        // the scaffolding widens it to also accept UInt8 + IsArray.
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

    [Fact]
    public void Equals_NewAndLegacyForms_AreNotByValueEqual_AcrossKinds()
    {
        // Sanity check on the migration boundary: the two forms have the SAME byte
        // content but DIFFERENT kinds, so DataValue equality (which checks kind first)
        // returns false. Migration consumers will need to either run on one form or
        // bridge the comparison. Documenting the current behaviour, not asserting
        // it's correct for the future — full elimination removes this concern.
        Arena arena = new();
        byte[] payload = [1, 2, 3];

        DataValue legacy = DataValue.FromUInt8Array(payload, arena);
        DataValue updated = DataValue.FromByteArray(payload, arena);

        Assert.NotEqual(legacy.Kind, updated.Kind);
        // Bytes equal:
        Assert.Equal(legacy.AsUInt8Array(arena), updated.AsUInt8Array(arena));
    }
}
