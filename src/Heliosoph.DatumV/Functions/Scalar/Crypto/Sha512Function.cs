using System.Security.Cryptography;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Crypto;

/// <summary>
/// SHA-512 hash. Accepts a string (UTF-8) or a byte array; returns a 64-byte
/// <see cref="DataKind.UInt8"/>[] digest.
/// </summary>
public sealed class Sha512Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sha512";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Crypto;

    /// <inheritdoc />
    public static string Description =>
        "Computes the SHA-512 hash (64 bytes). Accepts a string (hashed as UTF-8) "
        + "or a UInt8 array.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        HashFunctionSignatures.Build();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Sha512Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }

        byte[] digest = new byte[64];
        if (input.Kind == DataKind.String)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(input.AsString());
            SHA512.HashData(utf8, digest);
        }
        else
        {
            SHA512.HashData(input.AsByteSpan(), digest);
        }
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, digest, isArray: true));
    }
}
