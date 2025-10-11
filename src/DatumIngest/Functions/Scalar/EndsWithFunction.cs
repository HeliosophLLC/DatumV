using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether a string ends with a given suffix using ordinal comparison.
/// <c>ends_with(string, suffix)</c> returns a Boolean indicating whether the suffix was found at the end.
/// </summary>
public sealed class EndsWithFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "ends_with";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("ends_with() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"ends_with() requires a String as the first argument, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"ends_with() requires a String as the second argument, got {argumentKinds[1]}.");
        }

        return DataKind.Boolean;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue suffix = arguments[1];

        if (input.IsNull || suffix.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        bool result = input.AsString().EndsWith(suffix.AsString(), StringComparison.Ordinal);
        return DataValue.FromBoolean(result);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        DataValue suffix = arguments[1];

        if (input.IsNull || suffix.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        bool result = input
            .AsUtf8Span(store)
            .EndsWith(suffix.AsUtf8Span(store));
        return DataValue.FromBoolean(result);
    }
}
