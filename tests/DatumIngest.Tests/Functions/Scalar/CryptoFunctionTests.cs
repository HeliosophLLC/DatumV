using System.Security.Cryptography;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Crypto;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for cryptographic hash functions and the pgcrypto-style
/// <c>digest()</c> dispatcher. Hash outputs are compared against
/// <see cref="System.Security.Cryptography"/> reference results to ensure
/// the function plumbing doesn't subtly corrupt the digest.
/// </summary>
public sealed class CryptoFunctionTests
{
    private static readonly EvaluationFrame Frame = default;
    private const string Sample = "hello world";

    private static ValueRef Utf8Bytes(string s) =>
        ValueRef.FromBytes(DataKind.UInt8, Encoding.UTF8.GetBytes(s), isArray: true);

    // ─── md5 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Md5_String_KnownVector()
    {
        ValueRef result = await new Md5Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        Assert.False(result.IsNull);
        // Reference vector — RFC 1321 / standard test corpus.
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", result.AsString());
    }

    [Fact]
    public async Task Md5_ByteArray_MatchesStringForUtf8()
    {
        ValueRef strResult = await new Md5Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        ValueRef byteResult = await new Md5Function()
            .ExecuteAsync(new[] { Utf8Bytes(Sample) }, Frame, default);
        Assert.Equal(strResult.AsString(), byteResult.AsString());
    }

    [Fact]
    public async Task Md5_Null_ReturnsNullString()
    {
        ValueRef result = await new Md5Function()
            .ExecuteAsync(new[] { ValueRef.Null(DataKind.String) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Md5_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<Md5Function>(registry.TryGetScalar("md5"));
    }

    // ─── sha1 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sha1_String_MatchesBcl()
    {
        ValueRef result = await new Sha1Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        byte[] expected = SHA1.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
        Assert.Equal(20, result.AsBytes().Length);
    }

    [Fact]
    public async Task Sha1_Null_ReturnsNullByteArray()
    {
        ValueRef result = await new Sha1Function()
            .ExecuteAsync(new[] { ValueRef.Null(DataKind.String) }, Frame, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.UInt8, result.Kind);
        Assert.True(result.IsArray);
    }

    // ─── sha256 / sha384 / sha512 ──────────────────────────────────────────

    [Fact]
    public async Task Sha256_String_MatchesBcl()
    {
        ValueRef result = await new Sha256Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
        Assert.Equal(32, result.AsBytes().Length);
    }

    [Fact]
    public async Task Sha256_ByteArrayInput_MatchesBcl()
    {
        ValueRef result = await new Sha256Function()
            .ExecuteAsync(new[] { Utf8Bytes(Sample) }, Frame, default);
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
    }

    [Fact]
    public async Task Sha384_String_MatchesBcl()
    {
        ValueRef result = await new Sha384Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        byte[] expected = SHA384.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
        Assert.Equal(48, result.AsBytes().Length);
    }

    [Fact]
    public async Task Sha512_String_MatchesBcl()
    {
        ValueRef result = await new Sha512Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        byte[] expected = SHA512.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
        Assert.Equal(64, result.AsBytes().Length);
    }

    [Fact]
    public void HashFunctions_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<Sha1Function>(registry.TryGetScalar("sha1"));
        Assert.IsType<Sha256Function>(registry.TryGetScalar("sha256"));
        Assert.IsType<Sha384Function>(registry.TryGetScalar("sha384"));
        Assert.IsType<Sha512Function>(registry.TryGetScalar("sha512"));
    }

    // ─── digest ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("md5", 16)]
    [InlineData("sha1", 20)]
    [InlineData("sha256", 32)]
    [InlineData("sha384", 48)]
    [InlineData("sha512", 64)]
    public async Task Digest_KnownAlgorithms_ReturnExpectedLength(string algorithm, int length)
    {
        ValueRef result = await new DigestFunction().ExecuteAsync(
            new[] { ValueRef.FromString(Sample), ValueRef.FromString(algorithm) },
            Frame,
            default);
        Assert.False(result.IsNull);
        Assert.Equal(length, result.AsBytes().Length);
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("SHA_256")]
    [InlineData("sha 256")]
    [InlineData("Sha256")]
    public async Task Digest_AlgorithmNameNormalised(string spelling)
    {
        ValueRef result = await new DigestFunction().ExecuteAsync(
            new[] { ValueRef.FromString(Sample), ValueRef.FromString(spelling) },
            Frame,
            default);
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(expected, result.AsBytes());
    }

    [Fact]
    public async Task Digest_MatchesStandaloneSha256()
    {
        ValueRef viaDigest = await new DigestFunction().ExecuteAsync(
            new[] { ValueRef.FromString(Sample), ValueRef.FromString("sha256") },
            Frame,
            default);
        ValueRef viaStandalone = await new Sha256Function()
            .ExecuteAsync(new[] { ValueRef.FromString(Sample) }, Frame, default);
        Assert.Equal(viaStandalone.AsBytes(), viaDigest.AsBytes());
    }

    [Fact]
    public async Task Digest_UnknownAlgorithm_Throws()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DigestFunction().ExecuteAsync(
                new[] { ValueRef.FromString(Sample), ValueRef.FromString("blake2b") },
                Frame,
                default));
        Assert.Contains("unknown hash algorithm", ex.Message);
    }

    [Fact]
    public async Task Digest_Sha224_ThrowsWithGuidance()
    {
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new DigestFunction().ExecuteAsync(
                new[] { ValueRef.FromString(Sample), ValueRef.FromString("sha224") },
                Frame,
                default));
        Assert.Contains("sha224", ex.Message);
        Assert.Contains("sha256", ex.Message);
    }

    [Fact]
    public async Task Digest_NullData_PropagatesNull()
    {
        ValueRef result = await new DigestFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String), ValueRef.FromString("sha256") },
            Frame,
            default);
        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
    }

    [Fact]
    public void Digest_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<DigestFunction>(registry.TryGetScalar("digest"));
    }
}
