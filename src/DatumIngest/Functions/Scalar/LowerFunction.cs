using System.Buffers;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a string to lower-case using invariant culture rules.
/// <c>lower(string)</c> returns the input with all characters converted to their lower-case equivalents.
/// </summary>
public sealed class LowerFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "lower";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("lower() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"lower() requires a String argument, got {argumentKinds[0]}.");
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

        return DataValue.FromString(input.AsString().ToLowerInvariant());
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
        char[] resultBuf = ArrayPool<char>.Shared.Rent(span.Length);
        int written = span.ToLowerInvariant(resultBuf);
        DataValue result = DataValue.FromCharSpan(resultBuf.AsSpan(0, written), store);
        ArrayPool<char>.Shared.Return(resultBuf);
        ArrayPool<char>.Shared.Return(rented);
        return result;
    }
}
