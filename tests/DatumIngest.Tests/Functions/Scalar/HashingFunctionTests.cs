using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for hashing and encoding scalar functions: md5, sha256, sha512, crc32,
/// base64_encode, base64_decode, hex_encode, hex_decode, bytes_concat, bytes_slice, and bytes.
/// </summary>
public class HashingFunctionTests
{
    // ───────────────── Md5Function ─────────────────

    [Fact]
    public void Md5Function_HashesString()
    {
        Md5Function function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(DataKind.UInt8Array, result.Kind);
        HexEncodeFunction hexEncode = new();
        DataValue hex = hexEncode.Execute([result]);
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", hex.AsString());
    }

    [Fact]
    public void Md5Function_HashesBytes()
    {
        Md5Function function = new();
        byte[] inputBytes = [104, 101, 108, 108, 111]; // "hello" in UTF-8
        DataValue result = function.Execute([DataValue.FromUInt8Array(inputBytes)]);
        Assert.Equal(DataKind.UInt8Array, result.Kind);
        HexEncodeFunction hexEncode = new();
        DataValue hex = hexEncode.Execute([result]);
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", hex.AsString());
    }

    [Fact]
    public void Md5Function_NullInput_ReturnsNull()
    {
        Md5Function function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Sha256Function ─────────────────

    [Fact]
    public void Sha256Function_HashesString()
    {
        Sha256Function function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(DataKind.UInt8Array, result.Kind);
        HexEncodeFunction hexEncode = new();
        DataValue hex = hexEncode.Execute([result]);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hex.AsString());
    }

    [Fact]
    public void Sha256Function_NullInput_ReturnsNull()
    {
        Sha256Function function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Sha512Function ─────────────────

    [Fact]
    public void Sha512Function_HashesString()
    {
        Sha512Function function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(DataKind.UInt8Array, result.Kind);
        HexEncodeFunction hexEncode = new();
        DataValue hex = hexEncode.Execute([result]);
        string digest = hex.AsString();
        // Known SHA-512 digest of "hello" — verify the first 32 hex characters.
        Assert.StartsWith("9b71d224bd62f3785d96d46ad3ea3d73", digest);
        Assert.Equal(128, digest.Length);
    }

    [Fact]
    public void Sha512Function_NullInput_ReturnsNull()
    {
        Sha512Function function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Crc32Function ─────────────────

    [Fact]
    public void Crc32Function_ReturnsScalar()
    {
        Crc32Function function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(DataKind.Scalar, result.Kind);
        Assert.False(result.IsNull);
    }

    [Fact]
    public void Crc32Function_NullInput_ReturnsNull()
    {
        Crc32Function function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Base64EncodeFunction ─────────────────

    [Fact]
    public void Base64EncodeFunction_EncodesBytes()
    {
        Base64EncodeFunction function = new();
        byte[] inputBytes = [72, 101, 108, 108, 111]; // "Hello"
        DataValue result = function.Execute([DataValue.FromUInt8Array(inputBytes)]);
        Assert.Equal("SGVsbG8=", result.AsString());
    }

    [Fact]
    public void Base64EncodeFunction_NullInput_ReturnsNull()
    {
        Base64EncodeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.UInt8Array)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── Base64DecodeFunction ─────────────────

    [Fact]
    public void Base64DecodeFunction_DecodesString()
    {
        Base64DecodeFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("SGVsbG8=")]);
        byte[] expected = [72, 101, 108, 108, 111];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    [Fact]
    public void Base64DecodeFunction_NullInput_ReturnsNull()
    {
        Base64DecodeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── HexEncodeFunction ─────────────────

    [Fact]
    public void HexEncodeFunction_EncodesBytes()
    {
        HexEncodeFunction function = new();
        byte[] inputBytes = [0xDE, 0xAD];
        DataValue result = function.Execute([DataValue.FromUInt8Array(inputBytes)]);
        Assert.Equal("dead", result.AsString());
    }

    [Fact]
    public void HexEncodeFunction_NullInput_ReturnsNull()
    {
        HexEncodeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.UInt8Array)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── HexDecodeFunction ─────────────────

    [Fact]
    public void HexDecodeFunction_DecodesString()
    {
        HexDecodeFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("dead")]);
        byte[] expected = [0xDE, 0xAD];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    [Fact]
    public void HexDecodeFunction_NullInput_ReturnsNull()
    {
        HexDecodeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── BytesConcatFunction ─────────────────

    [Fact]
    public void BytesConcatFunction_ConcatenatesArrays()
    {
        BytesConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromUInt8Array([1, 2]),
            DataValue.FromUInt8Array([3, 4])
        ]);
        byte[] expected = [1, 2, 3, 4];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    [Fact]
    public void BytesConcatFunction_NullArgTreatedAsEmpty()
    {
        BytesConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromUInt8Array([1, 2]),
            DataValue.Null(DataKind.UInt8Array)
        ]);
        byte[] expected = [1, 2];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    // ───────────────── BytesSliceFunction ─────────────────

    [Fact]
    public void BytesSliceFunction_ExtractsSubArray()
    {
        BytesSliceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromUInt8Array([1, 2, 3, 4, 5]),
            DataValue.FromScalar(1),
            DataValue.FromScalar(3)
        ]);
        byte[] expected = [2, 3, 4];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    [Fact]
    public void BytesSliceFunction_NullInput_ReturnsNull()
    {
        BytesSliceFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.UInt8Array),
            DataValue.FromScalar(0),
            DataValue.FromScalar(1)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── BytesFunction ─────────────────

    [Fact]
    public void BytesFunction_ConstructsFromScalars()
    {
        BytesFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(65),
            DataValue.FromScalar(66),
            DataValue.FromScalar(67)
        ]);
        byte[] expected = [65, 66, 67];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }

    [Fact]
    public void BytesFunction_NullArgTreatedAsZero()
    {
        BytesFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(1),
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(3)
        ]);
        byte[] expected = [1, 0, 3];
        Assert.Equal(expected, result.AsUInt8Array().ToArray());
    }
}
