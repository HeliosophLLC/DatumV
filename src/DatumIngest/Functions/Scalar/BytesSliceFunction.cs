using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Extracts a sub-array from a byte array at a given start index for a given length.
/// <c>bytes_slice(bytes, start, length)</c> — uses 0-based indexing.
/// Indices are clamped to valid range (no out-of-bounds exceptions).
/// </summary>
public sealed class BytesSliceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "bytes_slice";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("bytes_slice() requires exactly 3 arguments: bytes, start, length.");
        }

        if (argumentKinds[0] != DataKind.UInt8Array)
        {
            throw new ArgumentException($"bytes_slice() first argument must be UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.Float32)
        {
            throw new ArgumentException($"bytes_slice() second argument must be Scalar, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] != DataKind.Float32)
        {
            throw new ArgumentException($"bytes_slice() third argument must be Scalar, got {argumentKinds[2]}.");
        }

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
        int start = (int)arguments[1].AsFloat32();
        int length = (int)arguments[2].AsFloat32();

        start = System.Math.Max(0, System.Math.Min(start, source.Length));
        length = System.Math.Max(0, System.Math.Min(length, source.Length - start));

        byte[] result = source.Slice(start, length).ToArray();
        return DataValue.FromUInt8Array(result);
    }
}
