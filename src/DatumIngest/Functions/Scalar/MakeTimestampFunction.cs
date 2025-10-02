using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a DateTime from year, month, day, hour, minute, and second components.
/// <c>make_timestamp(2024, 6, 15, 10, 30, 0)</c> returns 2024-06-15T10:30:00+00:00.
/// </summary>
/// <remarks>
/// All components are Scalar values. The result is always UTC (offset +00:00).
/// </remarks>
public sealed class MakeTimestampFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "make_timestamp";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 6)
        {
            throw new ArgumentException("make_timestamp() requires exactly 6 arguments: year, month, day, hour, minute, second (all Scalar).");
        }

        // PG: year, month, day, hour, minute are int; second is double precision
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 0, "year", argumentKinds[0]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 1, "month", argumentKinds[1]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, "day", argumentKinds[2]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 3, "hour", argumentKinds[3]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 4, "minute", argumentKinds[4]);

        if (!DataValue.IsNumericScalarKind(argumentKinds[5]))
        {
            throw new ArgumentException($"make_timestamp() argument 'second' must be numeric, got {argumentKinds[5]}.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        for (int i = 0; i < 6; i++)
        {
            if (arguments[i].IsNull)
            {
                return DataValue.Null(DataKind.DateTime);
            }
        }

        int year = arguments[0].ToInt32();
        int month = arguments[1].ToInt32();
        int day = arguments[2].ToInt32();
        int hour = arguments[3].ToInt32();
        int minute = arguments[4].ToInt32();
        double second = arguments[5].ToDouble();

        int wholeSec = (int)second;
        int ms = (int)((second - wholeSec) * 1000);

        return DataValue.FromDateTime(new DateTimeOffset(year, month, day, hour, minute, wholeSec, ms, TimeSpan.Zero));
    }
}
