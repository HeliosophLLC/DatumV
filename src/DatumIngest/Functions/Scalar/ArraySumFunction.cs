using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the sum of all non-null numeric elements in an <see cref="DataKind.Array"/>.
/// <c>array_sum(arr)</c> accepts arrays of <see cref="DataKind.Float32"/> or
/// <see cref="DataKind.UInt8"/> elements and always returns <see cref="DataKind.Float32"/>.
/// Null elements are skipped. Returns null if the array is null or all elements are null.
/// </summary>
public sealed class ArraySumFunction : IElementKindAwareFunction
{
    /// <inheritdoc />
    public string Name => "array_sum";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        ValidateArgumentCount(argumentKinds);
        return DataKind.Float32;
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
            if (!DataValueComparer.IsNumericScalar(elementKind))
            {
                throw new ArgumentException(
                    $"array_sum() requires an Array of numeric elements, got Array of {elementKind}.");
            }
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        double sum = 0;
        int count = 0;

        foreach (DataValue element in input.AsArray())
        {
            if (element.IsNull) continue;

            sum += element.ToFloat();
            count++;
        }

        return count == 0
            ? DataValue.Null(DataKind.Float32)
            : DataValue.FromFloat32((float)sum);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        double sum = 0;
        int count = 0;

        foreach (DataValue element in input.AsArray(store))
        {
            if (element.IsNull) continue;

            sum += element.ToFloat();
            count++;
        }

        return count == 0
            ? DataValue.Null(DataKind.Float32)
            : DataValue.FromFloat32((float)sum);
    }

    private static void ValidateArgumentCount(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("array_sum() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.Array)
        {
            throw new ArgumentException(
                $"array_sum() requires an Array argument, got {argumentKinds[0]}.");
        }
    }
}
