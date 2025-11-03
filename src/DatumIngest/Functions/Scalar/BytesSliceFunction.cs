using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a sub-array from a byte array at a given start index for a given length.
/// <c>bytes_slice(bytes, start, length)</c> — uses 0-based indexing.
/// Indices are clamped to valid range (no out-of-bounds exceptions).
/// </summary>
public sealed class BytesSliceFunction : IScalarFunction
{
    private static readonly string[] ArgumentNamesArray = ["bytes", "start", "length"];

    /// <inheritdoc />
    public string Name => "bytes_slice";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfArgumentCountMismatch(Name, argumentKinds.Length, ArgumentNamesArray);
        FunctionArgumentException.ThrowIfArgumentKindMismatch(Name, 0, ArgumentNamesArray[0], DataKind.UInt8Array, argumentKinds[0]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 1, ArgumentNamesArray[1], argumentKinds[1]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, ArgumentNamesArray[2], argumentKinds[2]);

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        ReadOnlyMemory<byte> source = arguments[0].AsUInt8Array();
        int start = arguments[1].ToInt32();
        int length = arguments[2].ToInt32();

        start = System.Math.Max(0, System.Math.Min(start, source.Length));
        length = System.Math.Max(0, System.Math.Min(length, source.Length - start));

        byte[] result = source.Slice(start, length).ToArray();
        return DataValue.FromUInt8Array(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.UInt8Array);
        }

        ReadOnlyMemory<byte> source = arguments[0].AsUInt8Array(store);
        int start = arguments[1].ToInt32();
        int length = arguments[2].ToInt32();

        start = System.Math.Max(0, System.Math.Min(start, source.Length));
        length = System.Math.Max(0, System.Math.Min(length, source.Length - start));

        byte[] result = source.Slice(start, length).ToArray();
        return DataValue.FromByteArray(result, store);
    }
}
