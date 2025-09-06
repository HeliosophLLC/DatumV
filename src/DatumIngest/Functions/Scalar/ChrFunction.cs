using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns a single character from an ASCII/Unicode code point.
/// <c>chr(code)</c> accepts a numeric argument and returns the corresponding character.
/// </summary>
public sealed class ChrFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "chr";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("chr() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException($"chr() requires a numeric argument, got {argumentKinds[0]}.");
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

        int codePoint = (int)(input.Kind is DataKind.UInt8
            ? input.AsUInt8()
            : input.AsFloat32());

        return DataValue.FromString(((char)codePoint).ToString());
    }
}
