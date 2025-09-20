using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Encodes a scalar value as a two-element vector using sine and cosine for cyclical features.
/// <c>cyclical_encode(value, period)</c> returns <c>[sin(2π·value/period), cos(2π·value/period)]</c>.
/// </summary>
/// <remarks>
/// Designed for temporal feature encoding:
/// <c>cyclical_encode(date_part('month', d), 12)</c> encodes the month as a point on the unit circle,
/// preserving the cyclical relationship between December (12) and January (1).
/// </remarks>
public sealed class CyclicalEncodeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "cyclical_encode";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("cyclical_encode() requires exactly 2 arguments: value (Scalar) and period (Scalar).");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException($"cyclical_encode() first argument must be a numeric scalar, got {argumentKinds[0]}.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[1]))
        {
            throw new ArgumentException($"cyclical_encode() second argument must be a numeric scalar, got {argumentKinds[1]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue period = arguments[1];

        if (input.IsNull || period.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        double angle = 2.0 * System.Math.PI * DataValueComparer.ToFloat(input) / DataValueComparer.ToFloat(period);
        float sinComponent = (float)System.Math.Sin(angle);
        float cosComponent = (float)System.Math.Cos(angle);

        return DataValue.FromVector([sinComponent, cosComponent]);
    }

}
