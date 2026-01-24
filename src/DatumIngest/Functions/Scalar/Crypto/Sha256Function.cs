using System.Security.Cryptography;
using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Crypto;

/// <summary>
/// SHA-256 hash. Accepts a string (UTF-8) or a byte array; returns a 32-byte
/// <see cref="DataKind.UInt8"/>[] digest.
/// </summary>
public sealed class Sha256Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sha256";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Crypto;

    /// <inheritdoc />
    public static string Description =>
        "Computes the SHA-256 hash (32 bytes). Accepts a string (hashed as UTF-8) "
        + "or a UInt8 array.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        HashFunctionSignatures.Build();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Sha256Function>(argumentKinds);

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

        byte[] digest = new byte[32];
        if (input.Kind == DataKind.String)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(input.AsString());
            SHA256.HashData(utf8, digest);
        }
        else
        {
            SHA256.HashData(input.AsByteSpan(), digest);
        }
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, digest, isArray: true));
    }
}
