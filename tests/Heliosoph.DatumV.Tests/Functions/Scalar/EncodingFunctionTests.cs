using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Encoding;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

/// <summary>
/// Tests for the PostgreSQL-compatible <c>encode</c> / <c>decode</c> pair.
/// Covers all three formats (base64, hex, escape) and the round-trip
/// property: <c>decode(encode(b, fmt), fmt) == b</c>.
/// </summary>
public sealed class EncodingFunctionTests
{
    private static readonly EvaluationFrame Frame = default;

    private static ValueRef Bytes(byte[] data) =>
        ValueRef.FromBytes(DataKind.UInt8, data, isArray: true);

    // ─── encode: known vectors ─────────────────────────────────────────────

    [Theory]
    [InlineData("hex", "68656c6c6f")]
    [InlineData("base64", "aGVsbG8=")]
    [InlineData("escape", "hello")]
    public async Task Encode_Hello_KnownOutput(string format, string expected)
    {
        ValueRef result = await new EncodeFunction().ExecuteAsync(
            new[] { Bytes(Encoding.UTF8.GetBytes("hello")), ValueRef.FromString(format) },
            Frame,
            default);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Encode_Escape_EscapesZeroBackslashAndHighBit()
    {
        byte[] input = new byte[] { 0x00, (byte)'a', (byte)'\\', 0xff };
        ValueRef result = await new EncodeFunction().ExecuteAsync(
            new[] { Bytes(input), ValueRef.FromString("escape") },
            Frame,
            default);
        // 0x00 -> \000, 'a' -> a, '\\' -> \\, 0xff -> \377
        Assert.Equal(@"\000a\\\377", result.AsString());
    }

    [Fact]
    public async Task Encode_FormatCaseInsensitive()
    {
        ValueRef result = await new EncodeFunction().ExecuteAsync(
            new[] { Bytes(new byte[] { 0xab, 0xcd }), ValueRef.FromString("HEX") },
            Frame,
            default);
        Assert.Equal("abcd", result.AsString());
    }

    [Fact]
    public async Task Encode_UnknownFormat_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new EncodeFunction().ExecuteAsync(
                new[] { Bytes(new byte[] { 0x01 }), ValueRef.FromString("rot13") },
                Frame,
                default));
        Assert.Contains("unknown encoding format", ex.Message);
    }

    [Fact]
    public async Task Encode_NullBytes_PropagatesNull()
    {
        ValueRef result = await new EncodeFunction().ExecuteAsync(
            new[] { ValueRef.NullArray(DataKind.UInt8), ValueRef.FromString("hex") },
            Frame,
            default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    // ─── decode: known vectors ─────────────────────────────────────────────

    [Theory]
    [InlineData("hex", "68656c6c6f")]
    [InlineData("base64", "aGVsbG8=")]
    [InlineData("escape", "hello")]
    public async Task Decode_Hello_KnownOutput(string format, string source)
    {
        ValueRef result = await new DecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromString(source), ValueRef.FromString(format) },
            Frame,
            default);
        Assert.False(result.IsNull);
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), result.AsBytes());
    }

    [Fact]
    public async Task Decode_Escape_DecodesOctalAndDoubleBackslash()
    {
        ValueRef result = await new DecodeFunction().ExecuteAsync(
            new[] { ValueRef.FromString(@"\000a\\\377"), ValueRef.FromString("escape") },
            Frame,
            default);
        Assert.Equal(new byte[] { 0x00, (byte)'a', (byte)'\\', 0xff }, result.AsBytes());
    }

    [Fact]
    public async Task Decode_InvalidHex_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DecodeFunction().ExecuteAsync(
                new[] { ValueRef.FromString("zz"), ValueRef.FromString("hex") },
                Frame,
                default));
        Assert.Contains("hex", ex.Message);
    }

    [Fact]
    public async Task Decode_InvalidBase64_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DecodeFunction().ExecuteAsync(
                new[] { ValueRef.FromString("not!base64!"), ValueRef.FromString("base64") },
                Frame,
                default));
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public async Task Decode_InvalidEscape_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DecodeFunction().ExecuteAsync(
                // \9 is not a valid escape (9 is not an octal digit).
                new[] { ValueRef.FromString(@"\9ab"), ValueRef.FromString("escape") },
                Frame,
                default));
        Assert.Contains("escape", ex.Message);
    }

    [Fact]
    public async Task Decode_HighBitWithoutEscape_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DecodeFunction().ExecuteAsync(
                new[] { ValueRef.FromString("aÿb"), ValueRef.FromString("escape") },
                Frame,
                default));
        Assert.Contains("non-ASCII", ex.Message);
    }

    [Fact]
    public async Task Decode_NullText_PropagatesNull()
    {
        ValueRef result = await new DecodeFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromString("hex") },
            Frame,
            default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.UInt8, result.Kind);
        Assert.True(result.IsArray);
    }

    // ─── round-trip ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hex")]
    [InlineData("base64")]
    [InlineData("escape")]
    public async Task EncodeThenDecode_RoundTrips(string format)
    {
        byte[] original = new byte[256];
        for (int i = 0; i < 256; i++) original[i] = (byte)i;

        ValueRef encoded = await new EncodeFunction().ExecuteAsync(
            new[] { Bytes(original), ValueRef.FromString(format) },
            Frame,
            default);
        ValueRef decoded = await new DecodeFunction().ExecuteAsync(
            new[] { encoded, ValueRef.FromString(format) },
            Frame,
            default);
        Assert.Equal(original, decoded.AsBytes());
    }

    // ─── registration ───────────────────────────────────────────────────────

    [Fact]
    public void EncodeAndDecode_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<EncodeFunction>(registry.TryGetScalar("encode"));
        Assert.IsType<DecodeFunction>(registry.TryGetScalar("decode"));
    }
}
