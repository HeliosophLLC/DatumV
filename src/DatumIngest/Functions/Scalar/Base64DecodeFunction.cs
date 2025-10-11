using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Decodes a Base64-encoded string into a byte array.
/// <c>base64_decode(string)</c> — accepts a String argument.
/// </summary>
public sealed class Base64DecodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "base64_decode";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("base64_decode() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"base64_decode() argument must be String, got {argumentKinds[0]}.");
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

        byte[] result = Convert.FromBase64String(input.AsString());
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
        int maxBytes = (chars.Length * 3 + 3) / 4; // base64 decode upper bound
        byte[] result = new byte[maxBytes];
        Convert.TryFromBase64Chars(chars, result, out int bytesWritten);
        System.Buffers.ArrayPool<char>.Shared.Return(rented);
        return DataValue.FromUInt8Array(result.AsSpan(0, bytesWritten).ToArray(), store);
    }
}
