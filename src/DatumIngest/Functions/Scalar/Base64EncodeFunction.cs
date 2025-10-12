using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Encodes a byte array as a Base64 string.
/// <c>base64_encode(bytes)</c> — accepts a UInt8Array argument.
/// </summary>
public sealed class Base64EncodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "base64_encode";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("base64_encode() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.UInt8Array)
        {
            throw new ArgumentException($"base64_encode() argument must be UInt8Array, got {argumentKinds[0]}.");
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

        byte[] inputBytes = input.AsUInt8Array().ToArray();
        return DataValue.FromString(Convert.ToBase64String(inputBytes));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        byte[] inputBytes = input.AsUInt8Array().ToArray();
        return DataValue.FromString(Convert.ToBase64String(inputBytes), store);
    }
}
