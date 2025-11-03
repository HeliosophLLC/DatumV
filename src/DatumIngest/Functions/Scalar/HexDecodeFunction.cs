using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Decodes a hexadecimal string into a byte array.
/// <c>hex_decode(string)</c> — accepts a String argument.
/// </summary>
public sealed class HexDecodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "hex_decode";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("hex_decode() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"hex_decode() argument must be String, got {argumentKinds[0]}.");
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

        byte[] result = Convert.FromHexString(input.AsString());
        return DataValue.FromUInt8Array(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        ReadOnlySpan<char> chars = input.AsStringSpan(store, out char[] rented);
        byte[] result = Convert.FromHexString(chars);
        System.Buffers.ArrayPool<char>.Shared.Return(rented);
        return DataValue.FromByteArray(result, store);
    }
}
