using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a Time value from hour, minute, and second components.
/// <c>make_time(hour, minute, second)</c> — all three arguments are required Scalars.
/// </summary>
public sealed class MakeTimeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "make_time";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("make_time() requires exactly 3 arguments: hour, minute, second.");
        }

        for (int index = 0; index < argumentKinds.Length; index++)
        {
            if (argumentKinds[index] != DataKind.Scalar)
            {
                throw new ArgumentException($"make_time() argument {index + 1} must be Scalar, got {argumentKinds[index]}.");
            }
        }

        return DataKind.Time;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.Time);
        }

        int hour = (int)arguments[0].AsScalar();
        int minute = (int)arguments[1].AsScalar();
        int second = (int)arguments[2].AsScalar();

        return DataValue.FromTime(new TimeOnly(hour, minute, second));
    }
}
