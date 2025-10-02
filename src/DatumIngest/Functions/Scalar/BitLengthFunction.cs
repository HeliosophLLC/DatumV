using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of bits in a string (8 × octet_length).
/// <c>bit_length(string)</c> — PostgreSQL compatible.
/// </summary>
public sealed class BitLengthFunction : IScalarFunction
{
    private static readonly string[] ArgumentNamesArray = ["value"];

    /// <inheritdoc />
    public string Name => "bit_length";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfArgumentCountMismatch(Name, argumentKinds.Length, ArgumentNamesArray);
        FunctionArgumentException.ThrowIfNotStringArgument(Name, 0, ArgumentNamesArray[0], argumentKinds[0]);

        return DataKind.Int32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Int32);
        }

        return DataValue.FromInt32(Encoding.UTF8.GetByteCount(input.AsString()) * 8);
    }
}
