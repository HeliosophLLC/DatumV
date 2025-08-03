using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the average of all non-null numeric elements in an <see cref="DataKind.Array"/>.
/// <c>array_avg(arr)</c> accepts arrays of <see cref="DataKind.Scalar"/> or
/// <see cref="DataKind.UInt8"/> elements and always returns <see cref="DataKind.Scalar"/>.
/// Null elements are skipped. Returns null if the array is null or all elements are null.
/// </summary>
public sealed class ArrayAvgFunction : IElementKindAwareFunction
{
    /// <inheritdoc />
    public string Name => "array_avg";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataKind ValidateArgumentsWithElementKinds(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataKind?> arrayElementKinds)
    {
        if (argumentKinds.Length != 1 || argumentKinds[0] != DataKind.Array)
        {
            ValidateArgumentCount(argumentKinds);
        }

        // Validate element kind when known at plan time.
        if (arrayElementKinds.Length > 0 && arrayElementKinds[0] is DataKind elementKind)
        {
            if (elementKind is not DataKind.Scalar and not DataKind.UInt8)
            {
                throw new ArgumentException(
                    $"array_avg() requires an Array of Scalar or UInt8 elements, got Array of {elementKind}.");
            }
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        double sum = 0;
        int count = 0;

        foreach (DataValue element in input.AsArray())
        {
            if (element.IsNull) continue;

            sum += element.Kind == DataKind.UInt8
                ? element.AsUInt8()
                : element.AsScalar();
            count++;
        }

        return count == 0
            ? DataValue.Null(DataKind.Scalar)
            : DataValue.FromScalar((float)(sum / count));
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_avg() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_avg() requires an Array argument, got {argumentKinds[0]}.");
        }
    }
}
