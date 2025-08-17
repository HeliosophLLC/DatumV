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

        for (int i = 0; i < 6; i++)
        {
            if (argumentKinds[i] != DataKind.Float32)
            {
                throw new ArgumentException($"make_timestamp() argument {i + 1} must be Scalar, got {argumentKinds[i]}.");
            }
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

        int year = (int)arguments[0].AsFloat32();
        int month = (int)arguments[1].AsFloat32();
        int day = (int)arguments[2].AsFloat32();
        int hour = (int)arguments[3].AsFloat32();
        int minute = (int)arguments[4].AsFloat32();
        int second = (int)arguments[5].AsFloat32();

        return DataValue.FromDateTime(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero));
    }
}
