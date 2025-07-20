using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Formats a Date or DateTime value as a string using a .NET format string.
/// <c>strftime(date_col, 'yyyy-MM-dd')</c> returns a formatted String.
/// </summary>
/// <remarks>
/// Uses .NET custom date and time format strings (e.g., "yyyy", "MM/dd/yyyy", "HH:mm:ss").
/// Common specifiers: yyyy (4-digit year), MM (2-digit month), dd (2-digit day),
/// HH (24-hour hour), mm (minute), ss (second), fff (millisecond).
/// </remarks>
public sealed class StrftimeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "strftime";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("strftime() requires exactly 2 arguments: date (Date/DateTime) and format (String).");
        }

        if (argumentKinds[0] is not (DataKind.Date or DataKind.DateTime))
        {
            throw new ArgumentException($"strftime() first argument must be Date or DateTime, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException($"strftime() second argument must be String (format), got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue dateValue = arguments[0];
        DataValue formatValue = arguments[1];

        if (dateValue.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string format = formatValue.AsString();

        string result = dateValue.Kind == DataKind.Date
            ? dateValue.AsDate().ToString(format, CultureInfo.InvariantCulture)
            : dateValue.AsDateTime().ToString(format, CultureInfo.InvariantCulture);

        return DataValue.FromString(result);
    }
}
