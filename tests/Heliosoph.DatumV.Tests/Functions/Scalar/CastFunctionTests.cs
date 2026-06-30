using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="CastFunction"/>, <see cref="TryCastFunction"/>,
/// and <see cref="TypeofFunction"/>.
/// </summary>
public sealed class CastFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    // ─── cast ──────────────────────────────────────────────────────────────

    [Fact]
    public void Cast_Metadata()
    {
        Assert.Equal("cast", CastFunction.Name);
        Assert.Equal(FunctionCategory.Conversion, CastFunction.Category);
    }

    [Fact]
    public void Cast_Validate_AcceptsTypeLiteralTarget()
    {
        Assert.Equal(DataKind.String,
            new CastFunction().ValidateArguments([DataKind.Int32, DataKind.Type]));
    }

    [Fact]
    public void Cast_Validate_AcceptsStringTarget()
    {
        Assert.Equal(DataKind.String,
            new CastFunction().ValidateArguments([DataKind.Int32, DataKind.String]));
    }

    [Fact]
    public void Cast_Validate_RejectsWrongArity()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new CastFunction().ValidateArguments([DataKind.Int32]));
    }

    [Fact]
    public void Cast_Validate_RejectsBadTargetKind()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new CastFunction().ValidateArguments([DataKind.Int32, DataKind.Float32]));
    }

    [Fact]
    public async Task Cast_NullInput_ReturnsTypedNull()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32), ValueRef.FromType(DataKind.Float64) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float64, result.Kind);
    }

    [Fact]
    public async Task Cast_SameKind_PassesThrough()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(42), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(42, result.AsInt32());
    }

    [Theory]
    [InlineData(DataKind.Int32, DataKind.Float64, 42, 42.0)]
    [InlineData(DataKind.Int32, DataKind.Int64, 42, 42L)]
    public async Task Cast_NumericToNumeric(DataKind sourceKind, DataKind targetKind, int sourceValue, object expected)
    {
        ValueRef source = sourceKind == DataKind.Int32 ? ValueRef.FromInt32(sourceValue) : ValueRef.FromInt64(sourceValue);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { source, ValueRef.FromType(targetKind) },
            Frame, default);
        Assert.Equal(targetKind, result.Kind);
        if (targetKind == DataKind.Float64)
        {
            Assert.Equal((double)expected, result.AsFloat64());
        }
        else if (targetKind == DataKind.Int64)
        {
            Assert.Equal((long)expected, result.AsInt64());
        }
    }

    [Fact]
    public async Task Cast_NumericToString()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(42), ValueRef.FromType(DataKind.String) },
            Frame, default);
        Assert.Equal("42", result.AsString());
    }

    [Fact]
    public async Task Cast_StringToInt32()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("123"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(123, result.AsInt32());
    }

    [Fact]
    public async Task Cast_StringToFloat64()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("3.14"), ValueRef.FromType(DataKind.Float64) },
            Frame, default);
        Assert.Equal(3.14, result.AsFloat64(), precision: 5);
    }

    [Fact]
    public async Task Cast_BooleanToInt32()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromBoolean(true), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public async Task Cast_StringToBoolean()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("true"), ValueRef.FromType(DataKind.Boolean) },
            Frame, default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task Cast_StringToDate()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("2026-04-29"), ValueRef.FromType(DataKind.Date) },
            Frame, default);
        Assert.Equal(new DateOnly(2026, 4, 29), result.AsDate());
    }

    [Fact]
    public async Task Cast_StringToTimestampTz_PreservesOffsetThenNormalises()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("2026-05-19T12:00:00-07:00"), ValueRef.FromType(DataKind.TimestampTz) },
            Frame, default);
        Assert.Equal(DataKind.TimestampTz, result.Kind);
        // FromTimestampTz normalises to UTC; the readback offset is zero.
        DateTimeOffset got = result.AsTimestampTz();
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 19, 0, 0, TimeSpan.Zero), got);
        Assert.Equal(TimeSpan.Zero, got.Offset);
    }

    [Fact]
    public async Task Cast_StringToTimestamp_StoresNaiveTicks()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("2026-05-19T12:00:00"), ValueRef.FromType(DataKind.Timestamp) },
            Frame, default);
        Assert.Equal(DataKind.Timestamp, result.Kind);
        DateTime got = result.AsTimestamp();
        Assert.Equal(new DateTime(2026, 5, 19, 12, 0, 0).Ticks, got.Ticks);
    }

    [Fact]
    public async Task Cast_TimestampTzToTimestamp_DropsZoneInfo()
    {
        DateTimeOffset src = new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromTimestampTz(src), ValueRef.FromType(DataKind.Timestamp) },
            Frame, default);
        Assert.Equal(DataKind.Timestamp, result.Kind);
        Assert.Equal(src.UtcDateTime.Ticks, result.AsTimestamp().Ticks);
    }

    [Fact]
    public async Task Cast_TimestampToTimestampTz_AssumesUtc()
    {
        // Documented PG divergence: no session TZ, so we assume UTC.
        DateTime naive = new(2026, 5, 19, 12, 0, 0, DateTimeKind.Unspecified);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromTimestamp(naive), ValueRef.FromType(DataKind.TimestampTz) },
            Frame, default);
        Assert.Equal(DataKind.TimestampTz, result.Kind);
        Assert.Equal(new DateTimeOffset(naive.Ticks, TimeSpan.Zero), result.AsTimestampTz());
    }

    [Fact]
    public async Task Cast_DateToTimestamp_ProducesMidnight()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromDate(new DateOnly(2026, 5, 19)), ValueRef.FromType(DataKind.Timestamp) },
            Frame, default);
        Assert.Equal(DataKind.Timestamp, result.Kind);
        Assert.Equal(new DateTime(2026, 5, 19, 0, 0, 0).Ticks, result.AsTimestamp().Ticks);
    }

    [Fact]
    public async Task Cast_TargetAsString_AcceptsNameAndAlias()
    {
        ValueRef byName = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(1), ValueRef.FromString("Float64") },
            Frame, default);
        Assert.Equal(DataKind.Float64, byName.Kind);

        ValueRef byAlias = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.FromInt32(1), ValueRef.FromString("bool") },
            Frame, default);
        Assert.Equal(DataKind.Boolean, byAlias.Kind);
    }

    [Fact]
    public async Task Cast_UnsupportedPair_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { ValueRef.FromUuid(Guid.NewGuid()), ValueRef.FromType(DataKind.Int32) },
                Frame, default));
        Assert.Contains("does not support", ex.Message);
    }

    [Fact]
    public async Task Cast_ScalarToArrayAnnotation_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { ValueRef.FromString("1,2,3"), ValueRef.FromString("Array<Int32>") },
                Frame, default));
        // Error message points users at the right path — array construction
        // is a separate concern from type conversion.
        Assert.Contains("requires the source to already be Array", ex.Message);
        Assert.Contains("string_split", ex.Message);
    }

    [Fact]
    public async Task Cast_ArrayToScalarAnnotation_Throws()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromType(DataKind.Int32) },
                Frame, default));
        Assert.Contains("cannot convert Array<Int32>", ex.Message);
    }

    [Fact]
    public async Task Cast_ArrayToSameArrayAnnotation_PassesThrough()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.String,
            [ValueRef.FromString("a"), ValueRef.FromString("b")]);
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { arr, ValueRef.FromString("Array<String>") },
            Frame, default);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public async Task Cast_ArrayToDifferentArrayAnnotation_Throws()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Int32,
            [ValueRef.FromInt32(1), ValueRef.FromInt32(2)]);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromString("Array<Float64>") },
                Frame, default));
        Assert.Contains("requires the source to already be Array<Float64>", ex.Message);
    }

    [Fact]
    public async Task Cast_NullSourceToArrayAnnotation_ReturnsTypedNullArray()
    {
        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromString("Array<String>") },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public async Task Cast_MultiDimArrayToFlatArrayAnnotation_StripsShape()
    {
        // CAST(multi_dim AS T[]) intentionally flattens. Both `_inline.IsMultiDim`
        // AND the wrapper `_shape` must come back false so downstream 1-D-only
        // consumers (array_slice, single-index bracket) can read the value.
        //
        // Historical note (2026-05-31): an earlier AsFlatArray implementation
        // cleared only the wrapper shape; the inline carrier kept IsMultiDim
        // and leaked through, breaking array_slice. This test pins the
        // post-fix behaviour. Inverse direction: 2-D consumers
        // (pose_from_rgbd, array_resize_2d) that depended on the no-op CAST
        // must use the source without the CAST (see project_procedural_body_arena_lifetime.md).
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        ValueRef multi = ValueRef.FromPrimitiveMultiDimArray(data, [2, 3], DataKind.Float32);
        Assert.True(multi.IsMultiDim, "fixture precondition: source must be multi-dim");

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { multi, ValueRef.FromString("Array<Float32>") },
            Frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.False(result.IsMultiDim,
            "CAST(multi_dim AS T[]) must produce a fully-flat array — neither " +
            "the wrapper shape nor the inline IsMultiDim flag may survive. " +
            "Regressing this leaks the multi-dim shape through to consumers " +
            "that test IsMultiDim (array_slice, single-index bracket).");
        Assert.Equal(6, result.GetArrayLength());
    }

    [Fact]
    public async Task Cast_FlatArrayToSameKindAnnotation_NoOp()
    {
        // 1-D source through a same-kind CAST is the trivial identity case.
        // Pinned to make sure a future "always rebuild on CAST" refactor
        // doesn't accidentally copy 1-D inputs.
        float[] data = [10f, 20f, 30f];
        ValueRef flat = ValueRef.FromPrimitiveArray(data, DataKind.Float32);
        Assert.False(flat.IsMultiDim);

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { flat, ValueRef.FromString("Array<Float32>") },
            Frame, default);

        Assert.True(result.IsArray);
        Assert.False(result.IsMultiDim);
        Assert.Equal(3, result.GetArrayLength());
    }

    [Fact]
    public async Task Cast_FlatArrayToShapedArrayAnnotation_AttachesShape()
    {
        // CAST(flat AS Array<T>(h, w)) reshapes a flat row-major buffer into a
        // shape-aware rank-2 array — the inverse of the bare-annotation flatten
        // above, and what a shaped typed-DECLARE
        // (`DECLARE m Array<Float32>(2, 3) = …`) compiles to. This is the
        // runtime reshape that shape-consuming functions (array_resize_2d,
        // multi-index array_get) require.
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        ValueRef flat = ValueRef.FromPrimitiveArray(data, DataKind.Float32);
        Assert.False(flat.IsMultiDim, "fixture precondition: source is flat");

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { flat, ValueRef.FromString("Array<Float32>(2, 3)") },
            Frame, default);

        Assert.True(result.IsArray);
        Assert.True(result.IsMultiDim,
            "a shaped (rank-≥2) array annotation must attach the declared shape, "
            + "not flatten — only the bare Array<T> annotation flattens.");
        Assert.Equal(2, result.Ndim);
        Assert.Equal(6, result.GetArrayLength());
    }

    [Fact]
    public async Task Cast_MultiDimArrayToShapedArrayAnnotation_ReshapesDroppingLeadingOne()
    {
        // A model's infer() output arrives multi-dim with a leading batch dim
        // (e.g. [1, 2, 3]). A shaped DECLARE `Array<Float32>(2, 3)` reshapes it
        // to a clean rank-2 [2, 3] — replacing the prior shape, not flattening.
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        ValueRef multi = ValueRef.FromPrimitiveMultiDimArray(data, [1, 2, 3], DataKind.Float32);
        Assert.Equal(3, multi.Ndim);

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { multi, ValueRef.FromString("Array<Float32>(2, 3)") },
            Frame, default);

        Assert.True(result.IsMultiDim);
        Assert.Equal(2, result.Ndim);
        Assert.Equal(6, result.GetArrayLength());
    }

    [Fact]
    public async Task Cast_ShapedArrayAnnotation_ElementCountMismatch_Throws()
    {
        // The declared shape's product must equal the element count. A mismatch
        // is a body bug (wrong native dims) and surfaces eagerly rather than
        // silently truncating or padding.
        float[] data = [1f, 2f, 3f, 4f, 5f, 6f];
        ValueRef flat = ValueRef.FromPrimitiveArray(data, DataKind.Float32);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { flat, ValueRef.FromString("Array<Float32>(2, 2)") },
                Frame, default));
    }

    // ─── byte-array → encoded-media-blob (zero-copy tag flip) ────────────────

    // PNG magic bytes — first 8 bytes of any conforming PNG file. The CAST
    // validator only inspects the leading signature, so the rest of the buffer
    // can be padding without invalidating the test.
    private static byte[] PngMagic() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(DataKind.Video)]
    [InlineData(DataKind.PointCloud)]
    [InlineData(DataKind.Mesh)]
    public async Task Cast_ByteArrayToUnvalidatedBlob_RetagsBytesVerbatim(DataKind targetKind)
    {
        // Video / PointCloud / Mesh have no cheap header sniffer — the cast
        // is a verbatim retag, same operation image_decode performs for
        // images. Failure is caught downstream at decode/materialization.
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true);

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(targetKind) },
            Frame, default);

        Assert.Equal(targetKind, result.Kind);
        Assert.False(result.IsArray);
        Assert.Same(payload, result.AsBytes());
    }

    [Theory]
    [InlineData("png",      new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("jpeg",     new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 })]
    [InlineData("gif",      new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' })]
    [InlineData("bmp",      new byte[] { (byte)'B', (byte)'M', 0x46, 0x00, 0x00, 0x00 })]
    [InlineData("tiff-le",  new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 })]
    [InlineData("tiff-be",  new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 })]
    public async Task Cast_ByteArrayToImage_WithRecognisedMagic_RetagsBytes(string format, byte[] payload)
    {
        // Pins the validator's full magic-byte table. CAST and image_decode
        // should both accept anything in this set; image_decode is broader
        // (anything SkiaSharp recognises), CAST is restricted to formats with
        // a stable signature.
        _ = format; // present in the test id; used to disambiguate failures.
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true);

        ValueRef result = await new CastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(DataKind.Image) },
            Frame, default);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.Same(payload, result.AsBytes());
    }

    [Fact]
    public async Task Cast_ByteArrayToImage_WithGarbage_ThrowsHeaderError()
    {
        // Validation catches the obvious-garbage case at the CAST site rather
        // than letting SkiaSharp throw "stream is not a known image format"
        // ten frames deep on first image_width() / model invocation.
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, [0xDE, 0xAD, 0xBE, 0xEF], isArray: true);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { bytes, ValueRef.FromType(DataKind.Image) },
                Frame, default));
        Assert.Contains("cast() to Image:", ex.Message);
        Assert.Contains("DE AD BE EF", ex.Message);
        Assert.Contains("PNG / JPEG / WebP / GIF", ex.Message);
    }

    [Fact]
    public async Task Cast_ByteArrayToAudio_WithGarbage_ThrowsHeaderError()
    {
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, [0xDE, 0xAD, 0xBE, 0xEF], isArray: true);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { bytes, ValueRef.FromType(DataKind.Audio) },
                Frame, default));
        Assert.Contains("cast() to Audio:", ex.Message);
        Assert.Contains("WAV / FLAC / MP3 / OGG", ex.Message);
    }

    [Fact]
    public async Task Cast_ByteArrayToJson_StillRejected()
    {
        // Json bytes are CBOR-encoded internally, so a raw UInt8[] is
        // overwhelmingly likely to be JSON *text* — caller wants
        // CAST(string AS Json), not a raw retag. Pin the rejection.
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, [(byte)'{', (byte)'}'], isArray: true);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CastFunction().ExecuteAsync(
                new[] { bytes, ValueRef.FromType(DataKind.Json) },
                Frame, default));
        Assert.Contains("cannot convert Array<UInt8>", ex.Message);
    }

    [Fact]
    public async Task TryCast_ByteArrayToImage_WithGarbage_ReturnsTypedNull()
    {
        // try_cast must honour its "typed null on failure" contract even
        // when the pair is supported — validation failure counts as failure.
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, [0xDE, 0xAD, 0xBE, 0xEF], isArray: true);

        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(DataKind.Image) },
            Frame, default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task TryCast_ByteArrayToImage_WithRecognisedMagic_Succeeds()
    {
        byte[] payload = PngMagic();
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, payload, isArray: true);

        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(DataKind.Image) },
            Frame, default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Theory]
    [InlineData(DataKind.Image, false)]      // garbage doesn't match a known image header
    [InlineData(DataKind.Audio, false)]      // garbage doesn't match a known audio header
    [InlineData(DataKind.Video, true)]       // no cheap validator — passthrough
    [InlineData(DataKind.PointCloud, true)]  // no cheap validator — passthrough
    [InlineData(DataKind.Mesh, true)]        // no cheap validator — passthrough
    [InlineData(DataKind.Json, false)]       // Json bytes are CBOR; UInt8[] is intentionally rejected
    [InlineData(DataKind.String, false)]     // unsupported pair
    [InlineData(DataKind.Int32, false)]      // unsupported pair
    public async Task CanCast_GarbageBytesToScalar_ReportsValidatedKindsHonestly(DataKind targetKind, bool expected)
    {
        // can_cast must reflect what would actually succeed — that includes
        // header validation for Image / Audio. The passthrough kinds report
        // true because the engine has no cheap way to refute the bytes;
        // false would be lying about kinds it doesn't actually validate.
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, [0xDE, 0xAD, 0xBE, 0xEF], isArray: true);

        ValueRef result = await new CanCastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(targetKind) },
            Frame, default);

        Assert.Equal(expected, result.AsBoolean());
    }

    [Fact]
    public async Task CanCast_PngMagicBytesToImage_ReturnsTrue()
    {
        ValueRef bytes = ValueRef.FromBytes(DataKind.UInt8, PngMagic(), isArray: true);

        ValueRef result = await new CanCastFunction().ExecuteAsync(
            new[] { bytes, ValueRef.FromType(DataKind.Image) },
            Frame, default);

        Assert.True(result.AsBoolean());
    }

    // ─── try_cast ──────────────────────────────────────────────────────────

    [Fact]
    public void TryCast_Metadata()
    {
        Assert.Equal("try_cast", TryCastFunction.Name);
    }

    [Fact]
    public async Task TryCast_NullInput_ReturnsTypedNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task TryCast_ValidConversion_Succeeds()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("42"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.False(result.IsNull);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public async Task TryCast_BadParse_ReturnsNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromString("not-a-number"), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task TryCast_UnsupportedPair_ReturnsNull()
    {
        ValueRef result = await new TryCastFunction().ExecuteAsync(
            new[] { ValueRef.FromUuid(Guid.NewGuid()), ValueRef.FromType(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ─── typeof ────────────────────────────────────────────────────────────

    [Fact]
    public void Typeof_Metadata()
    {
        Assert.Equal("typeof", TypeofFunction.Name);
    }

    [Theory]
    [InlineData(DataKind.Int32)]
    [InlineData(DataKind.String)]
    [InlineData(DataKind.Float64)]
    public async Task Typeof_ReturnsKind(DataKind expected)
    {
        ValueRef input = expected switch
        {
            DataKind.Int32 => ValueRef.FromInt32(0),
            DataKind.String => ValueRef.FromString("x"),
            DataKind.Float64 => ValueRef.FromFloat64(0),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        ValueRef result = await new TypeofFunction().ExecuteAsync(new[] { input }, Frame, default);
        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(expected, result.AsType());
    }

    [Fact]
    public async Task Typeof_OnNull_ReturnsNullTypeValue()
    {
        // typeof(NULL) → NULL of kind Type. Aligns with the design that null
        // values have no inhabitable type identity — comparisons fall under
        // null-propagation, and downstream rendering shows "NULL" rather than
        // a type name that doesn't apply.
        ValueRef result = await new TypeofFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Int32) },
            Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Type, result.Kind);
    }

    // ─── registry ──────────────────────────────────────────────────────────

    [Fact]
    public void RegistrySees_AllThreeFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<CastFunction>(registry.TryGetScalar("cast"));
        Assert.IsType<TryCastFunction>(registry.TryGetScalar("try_cast"));
        Assert.IsType<TypeofFunction>(registry.TryGetScalar("typeof"));
    }
}
