using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Parses string representations of parameter values (from CLI arguments or
/// external configuration) into <see cref="DataValue"/> instances.
/// </summary>
public static class ParameterValueParser
{
    /// <summary>
    /// Parses a string value into a <see cref="DataValue"/> using type inference.
    /// Numbers parse as <see cref="DataKind.Scalar"/>, <c>true</c>/<c>false</c>
    /// as <see cref="DataKind.Boolean"/>, <c>null</c> as a typed null, and
    /// everything else as <see cref="DataKind.String"/>.
    /// </summary>
    /// <param name="value">The string representation of the parameter value.</param>
    /// <returns>The parsed data value.</returns>
    public static DataValue Parse(string value)
    {
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return DataValue.Null(DataKind.Scalar);
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return DataValue.FromBoolean(true);
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return DataValue.FromBoolean(false);
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number))
        {
            return DataValue.FromScalar((float)number);
        }

        return DataValue.FromString(value);
    }
}
