using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of bits in a string (8 × octet_length).
/// <c>bit_length(string)</c> — PostgreSQL compatible.
/// </summary>
public sealed class BitLengthFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "bit_length";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("bit_length() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"bit_length() requires a String argument, got {argumentKinds[0]}.");
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

        return DataValue.FromFloat32(Encoding.UTF8.GetByteCount(input.AsString()) * 8);
    }
}
