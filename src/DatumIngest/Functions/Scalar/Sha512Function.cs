using System.Security.Cryptography;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the SHA-512 hash of a string or byte array and returns the lowercase hex digest.
/// <c>sha512(input)</c> — accepts a String or UInt8Array argument.
/// </summary>
public sealed class Sha512Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "sha512";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("sha512() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String && argumentKinds[0] != DataKind.UInt8Array)
        {
            throw new ArgumentException($"sha512() argument must be String or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        byte[] inputBytes;
        if (input.Kind == DataKind.String)
        {
            inputBytes = Encoding.UTF8.GetBytes(input.AsString());
        }
        else
        {
            inputBytes = input.AsUInt8Array().ToArray();
        }

        byte[] hash = SHA512.HashData(inputBytes);
        return DataValue.FromString(Convert.ToHexStringLower(hash));
    }
}
