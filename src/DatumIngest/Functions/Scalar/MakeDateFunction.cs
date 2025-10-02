using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a Date from year, month, and day components.
/// <c>make_date(2024, 6, 15)</c> returns 2024-06-15.
/// </summary>
public sealed class MakeDateFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "make_date";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException("make_date() requires exactly 3 arguments: year, month, day (all Scalar).");
        }

        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 0, "year", argumentKinds[0]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 1, "month", argumentKinds[1]);
        FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, 2, "day", argumentKinds[2]);

        return DataKind.Date;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.Date);
        }

        int year = arguments[0].ToInt32();
        int month = arguments[1].ToInt32();
        int day = arguments[2].ToInt32();

        return DataValue.FromDate(new DateOnly(year, month, day));
    }
}
