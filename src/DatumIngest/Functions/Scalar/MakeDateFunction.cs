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

        for (int i = 0; i < 3; i++)
        {
            if (argumentKinds[i] != DataKind.Float32)
            {
                throw new ArgumentException($"make_date() argument {i + 1} must be Scalar, got {argumentKinds[i]}.");
            }
        }

        return DataKind.Date;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull || arguments[2].IsNull)
        {
            return DataValue.Null(DataKind.Date);
        }

        int year = (int)arguments[0].AsFloat32();
        int month = (int)arguments[1].AsFloat32();
        int day = (int)arguments[2].AsFloat32();

        return DataValue.FromDate(new DateOnly(year, month, day));
    }
}
