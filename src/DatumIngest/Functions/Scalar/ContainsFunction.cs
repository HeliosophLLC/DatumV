using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether a string contains a given substring using ordinal comparison.
/// <c>contains(string, substring)</c> returns a Boolean indicating whether the substring was found.
/// </summary>
public sealed class ContainsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "contains";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("contains() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"contains() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"contains() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue substring = arguments[1];

        if (input.IsNull || substring.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        bool result = input.AsString().Contains(substring.AsString(), StringComparison.Ordinal);
        return DataValue.FromBoolean(result);
    }
}
