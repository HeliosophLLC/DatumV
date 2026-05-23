using System.Security.Cryptography;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Crypto;

/// <summary>
/// pgcrypto-style <c>digest(data, algorithm)</c> dispatcher. Computes a
/// cryptographic hash of <c>data</c> (string or UInt8 array) using the named
/// algorithm and returns the raw digest as <see cref="DataKind.UInt8"/>[].
/// Supported algorithms are those shipped in the .NET BCL: <c>md5</c>,
/// <c>sha1</c>, <c>sha256</c>, <c>sha384</c>, <c>sha512</c>. Algorithm names
/// are case-insensitive and accept optional hyphens (e.g. <c>SHA-256</c>).
/// <c>sha224</c> is not supported (no BCL implementation).
/// </summary>
public sealed class DigestFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "digest";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Crypto;

    /// <inheritdoc />
    public static string Description =>
        "Computes the digest of `data` using the named hash algorithm. "
        + "Supported: md5, sha1, sha256, sha384, sha512. Returns UInt8[].";

    private static readonly string[] AlgorithmValues = ["md5", "sha1", "sha256", "sha384", "sha512"];

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("data", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("algorithm", DataKindMatcher.StringEnum(AlgorithmValues), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("data", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
                new ParameterSpec("algorithm", DataKindMatcher.StringEnum(AlgorithmValues), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DigestFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef data = args[0];
        ValueRef alg = args[1];

        // PG semantics: a null in either argument produces null.
        if (data.IsNull || alg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }

        string algorithm = NormaliseAlgorithm(alg.AsString());

        ReadOnlySpan<byte> input = data.Kind == DataKind.String
            ? System.Text.Encoding.UTF8.GetBytes(data.AsString())
            : data.AsByteSpan();

        byte[] digest = algorithm switch
        {
            "md5" => MD5.HashData(input),
            "sha1" => SHA1.HashData(input),
            "sha256" => SHA256.HashData(input),
            "sha384" => SHA384.HashData(input),
            "sha512" => SHA512.HashData(input),
            "sha224" => throw new FunctionArgumentException(
                Name,
                "algorithm 'sha224' is not supported (no .NET BCL implementation). "
                + "Use 'sha256' instead, or open an issue to request a managed SHA-224."),
            _ => throw new FunctionArgumentException(
                Name,
                $"unknown hash algorithm '{alg.AsString()}'. "
                + "Supported algorithms: md5, sha1, sha256, sha384, sha512."),
        };

        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, digest, isArray: true));
    }

    private static string NormaliseAlgorithm(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        int written = 0;
        foreach (char c in raw)
        {
            if (c == '-' || c == '_' || c == ' ')
            {
                continue;
            }
            buffer[written++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..written]);
    }
}
