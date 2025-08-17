using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Tests whether a string value can be parsed as a date.
/// <c>is_date(string_col)</c> returns 1 if the value parses as ISO 8601, 0 otherwise.
/// </summary>
/// <remarks>
/// For Date and DateTime inputs, always returns 1.
/// For String inputs, attempts ISO 8601 parsing with invariant culture.
/// For null inputs, returns null.
/// </remarks>
public sealed class IsDateFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_date";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("is_date() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.String or DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"is_date() requires a String, Date, or DateTime argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        // Already a Date or DateTime — trivially true.
        if (input.Kind is DataKind.Date or DataKind.DateTime)
        {
            return DataValue.FromFloat32(1f);
        }

        string text = input.AsString();
        bool canParse = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out _);

        return DataValue.FromFloat32(canParse ? 1f : 0f);
    }
}
