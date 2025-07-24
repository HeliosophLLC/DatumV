using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a Duration value from days, hours, minutes, and seconds.
/// <c>make_duration(days, hours, minutes, seconds)</c> — all four Scalar arguments are required.
/// </summary>
public sealed class MakeDurationFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "make_duration";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 4)
        {
            throw new ArgumentException("make_duration() requires exactly 4 arguments: days, hours, minutes, seconds.");
        }

        for (int index = 0; index < argumentKinds.Length; index++)
        {
            if (argumentKinds[index] != DataKind.Scalar)
            {
                throw new ArgumentException($"make_duration() argument {index + 1} must be Scalar, got {argumentKinds[index]}.");
            }
        }

        return DataKind.Duration;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].IsNull)
            {
                return DataValue.Null(DataKind.Duration);
            }
        }

        int days = (int)arguments[0].AsScalar();
        int hours = (int)arguments[1].AsScalar();
        int minutes = (int)arguments[2].AsScalar();
        int seconds = (int)arguments[3].AsScalar();

        return DataValue.FromDuration(new TimeSpan(days, hours, minutes, seconds));
    }
}
