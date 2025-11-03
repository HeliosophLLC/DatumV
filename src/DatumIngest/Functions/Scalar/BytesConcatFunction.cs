using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Concatenates two or more byte arrays into a single byte array.
/// <c>bytes_concat(a, b, ...)</c> accepts a variable number of UInt8Array arguments (minimum 2).
/// Null arguments are treated as empty (SQL concat semantics).
/// </summary>
public sealed class BytesConcatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "bytes_concat";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
        {
            throw new ArgumentException("bytes_concat() requires at least 2 arguments.");
        }

        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.UInt8Array)
            {
                throw new ArgumentException($"bytes_concat() requires all arguments to be UInt8Array, but argument {i} is {argumentKinds[i]}.");
            }
        }

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int totalLength = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull)
            {
                totalLength += arguments[i].AsUInt8Array().Length;
            }
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull)
            {
                ReadOnlyMemory<byte> segment = arguments[i].AsUInt8Array();
                segment.Span.CopyTo(result.AsSpan(offset));
                offset += segment.Length;
            }
        }

        return DataValue.FromUInt8Array(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        int totalLength = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull)
            {
                totalLength += arguments[i].AsUInt8Array(store).Length;
            }
        }

        byte[] result = new byte[totalLength];
        int offset = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull)
            {
                ReadOnlyMemory<byte> segment = arguments[i].AsUInt8Array(store);
                segment.Span.CopyTo(result.AsSpan(offset));
                offset += segment.Length;
            }
        }

        return DataValue.FromByteArray(result, store);
    }
}
