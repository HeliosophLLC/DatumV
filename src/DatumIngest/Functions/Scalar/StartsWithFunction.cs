using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether a string begins with a given prefix using ordinal comparison.
/// <c>starts_with(string, prefix)</c> returns a Boolean indicating whether the prefix was found at the start.
/// </summary>
public sealed class StartsWithFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "starts_with";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("starts_with() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"starts_with() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"starts_with() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue prefix = arguments[1];

        if (input.IsNull || prefix.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        bool result = input.AsString().StartsWith(prefix.AsString(), StringComparison.Ordinal);
        return DataValue.FromBoolean(result);
    }
}
