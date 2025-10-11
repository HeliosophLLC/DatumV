using System.Security.Cryptography;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the SHA-512 hash of a string or byte array and returns the raw hash bytes.
/// <c>sha512(input)</c> — accepts a String or UInt8Array argument. Returns UInt8Array.
/// </summary>
public sealed class Sha512Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "sha512";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

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

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
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
        return DataValue.FromUInt8Array(hash);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        if (input.Kind == DataKind.String)
        {
            ReadOnlySpan<byte> inputBytes = input.AsUtf8Span(store);
            byte[] hash = SHA512.HashData(inputBytes);
            return DataValue.FromUInt8Array(hash);
        }
        else
        {
            ReadOnlySpan<byte> inputBytes = input.AsUInt8Array();
            byte[] hash = SHA512.HashData(inputBytes);
            return DataValue.FromUInt8Array(hash);
        }
    }
}
