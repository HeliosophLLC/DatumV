using System.Security.Cryptography;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Crypto;

/// <summary>
/// PostgreSQL-compatible <c>md5</c>: accepts either a <see cref="DataKind.String"/>
/// (hashed as UTF-8) or a byte array (<see cref="DataKind.UInt8"/>[]) and
/// returns the 32-character lowercase hexadecimal digest as a string. Mirrors
/// Postgres' core <c>md5()</c>, where the text overload returns hex text
/// (the binary overload returning <c>bytea</c> is intentionally folded into
/// <c>digest('md5', …)</c>).
/// </summary>
public sealed class Md5Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "md5";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Crypto;

    /// <inheritdoc />
    public static string Description =>
        "Computes the MD5 hash of the input (UTF-8 for strings, raw bytes for UInt8 arrays) "
        + "and returns the 32-character lowercase hex digest.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("input", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("input", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Md5Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        Span<byte> digest = stackalloc byte[16];
        if (input.Kind == DataKind.String)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(input.AsString());
            MD5.HashData(utf8, digest);
        }
        else
        {
            MD5.HashData(input.AsByteSpan(), digest);
        }

        return new ValueTask<ValueRef>(ValueRef.FromString(Convert.ToHexStringLower(digest)));
    }
}
