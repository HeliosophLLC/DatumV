using System.IO.Hashing;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the CRC-32 checksum of a string or byte array and returns it as a scalar value.
/// <c>crc32(input)</c> — accepts a String or UInt8Array argument.
/// </summary>
public sealed class Crc32Function : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "crc32";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("crc32() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String && argumentKinds[0] != DataKind.UInt8Array)
        {
            throw new ArgumentException($"crc32() argument must be String or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
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

        uint crc = Crc32.HashToUInt32(inputBytes);
        return DataValue.FromFloat32(crc);
    }
}
