using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Replaces all occurrences of a substring within a string using ordinal comparison.
/// <c>replace(string, old, new)</c> returns the string with all matches of <c>old</c> replaced by <c>new</c>.
/// </summary>
public sealed class ReplaceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "replace";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("replace() requires exactly 3 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"replace() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"replace() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        if (argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException($"replace() requires a String as the third argument, got {argumentKinds[2]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue oldValue = arguments[1];
        DataValue newValue = arguments[2];

        if (input.IsNull || oldValue.IsNull || newValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string result = input.AsString().Replace(oldValue.AsString(), newValue.AsString(), StringComparison.Ordinal);
        return DataValue.FromString(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue oldValue = arguments[1];
        DataValue newValue = arguments[2];

        if (input.IsNull || oldValue.IsNull || newValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        // string.Replace requires string inputs; use AsString for all three and store for output.
        string result = input.AsString(store).Replace(oldValue.AsString(store), newValue.AsString(store), StringComparison.Ordinal);
        return DataValue.FromString(result, store);
    }
}
