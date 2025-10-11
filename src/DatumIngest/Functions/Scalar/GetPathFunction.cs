using System.IO;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the directory path from a file path string.
/// <c>get_path(path)</c>
/// </summary>
public sealed class GetPathFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "get_path";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("get_path() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"get_path() argument must be String, got {argumentKinds[0]}.");
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

        string? directoryPath = Path.GetDirectoryName(input.AsString());
        return DataValue.FromString(directoryPath ?? string.Empty);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<char> span = input.AsStringSpan(store, out char[] rented);
        ReadOnlySpan<char> directoryPath = Path.GetDirectoryName(span);
        DataValue result = DataValue.FromCharSpan(directoryPath, store);
        System.Buffers.ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
