using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Returns the rank (number of dimensions) of a vector, matrix, or tensor.
/// <c>rank(value)</c> returns 1 for vectors, 2 for matrices, and N for tensors.
/// </summary>
public sealed class RankFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "rank";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("rank() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"rank() does not support {argumentKinds[0]}.");
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

        return input.Kind switch
        {
            DataKind.Vector => DataValue.FromFloat32(1),
            DataKind.Matrix => DataValue.FromFloat32(2),
            DataKind.Tensor =>
                DataValue.FromFloat32(input.AsTensor(out int[] shape) is var _ ? shape.Length : 0),
            _ => throw new InvalidOperationException($"rank() does not support {input.Kind}.")
        };
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        return input.Kind switch
        {
            DataKind.Vector => DataValue.FromFloat32(1),
            DataKind.Matrix => DataValue.FromFloat32(2),
            DataKind.Tensor =>
                DataValue.FromFloat32(input.AsTensor(store, out int[] shape) is var _ ? shape.Length : 0),
            _ => throw new InvalidOperationException($"rank() does not support {input.Kind}.")
        };
    }
}

/// <summary>
/// Returns the size of a specific dimension: <c>rdim(value, axis)</c>.
/// For a vector, only axis 0 is valid and returns the length.
/// For a matrix, axis 0 returns rows and axis 1 returns columns.
/// For a tensor, axis 0..N-1 returns the corresponding dimension size.
/// </summary>
public sealed class RdimFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "rdim";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("rdim() requires exactly 2 arguments (value, axis).");
        }

        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"rdim() first argument must be a Vector, Matrix, or Tensor.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[1]))
        {
            throw new ArgumentException("rdim() second argument (axis) must be numeric.");
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

        int axis = arguments[1].ToInt32();

        switch (input.Kind)
        {
            case DataKind.Vector:
            {
                if (axis != 0)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-1 vector.");
                }
                return DataValue.FromFloat32(input.AsVector().Length);
            }

            case DataKind.Matrix:
            {
                if (axis is < 0 or > 1)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-2 matrix.");
                }
                input.AsMatrix(out int rows, out int columns);
                return DataValue.FromFloat32(axis == 0 ? rows : columns);
            }

            case DataKind.Tensor:
            {
                input.AsTensor(out int[] shape);
                if (axis < 0 || axis >= shape.Length)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-{shape.Length} tensor.");
                }
                return DataValue.FromFloat32(shape[axis]);
            }

            default:
                throw new InvalidOperationException($"rdim() does not support {input.Kind}.");
        }
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        int axis = arguments[1].ToInt32();

        switch (input.Kind)
        {
            case DataKind.Vector:
            {
                if (axis != 0)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-1 vector.");
                }
                return DataValue.FromFloat32(input.AsVector(store).Length);
            }

            case DataKind.Matrix:
            {
                if (axis is < 0 or > 1)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-2 matrix.");
                }
                input.AsMatrix(store, out int rows, out int columns);
                return DataValue.FromFloat32(axis == 0 ? rows : columns);
            }

            case DataKind.Tensor:
            {
                input.AsTensor(store, out int[] shape);
                if (axis < 0 || axis >= shape.Length)
                {
                    throw new InvalidOperationException(
                        $"rdim() axis {axis} is out of range for a rank-{shape.Length} tensor.");
                }
                return DataValue.FromFloat32(shape[axis]);
            }

            default:
                throw new InvalidOperationException($"rdim() does not support {input.Kind}.");
        }
    }
}

/// <summary>
/// Returns the shape of a vector, matrix, or tensor as a vector of dimension sizes.
/// <c>shape(value)</c> returns [length] for vectors, [rows, cols] for matrices,
/// and [d0, d1, ..., dN] for tensors.
/// </summary>
public sealed class ShapeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "shape";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("shape() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"shape() does not support {argumentKinds[0]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        return input.Kind switch
        {
            DataKind.Vector => DataValue.FromVector([input.AsVector().Length]),
            DataKind.Matrix => ShapeFromMatrix(input),
            DataKind.Tensor => ShapeFromTensor(input),
            _ => throw new InvalidOperationException($"shape() does not support {input.Kind}.")
        };
    }

    private static DataValue ShapeFromMatrix(DataValue input)
    {
        input.AsMatrix(out int rows, out int columns);
        return DataValue.FromVector([rows, columns]);
    }

    private static DataValue ShapeFromTensor(DataValue input)
    {
        input.AsTensor(out int[] shape);
        float[] result = new float[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            result[i] = shape[i];
        }
        return DataValue.FromVector(result);
    }

    private static DataValue ShapeFromMatrix(DataValue input, IValueStore store)
    {
        input.AsMatrix(store, out int rows, out int columns);
        return DataValue.FromVector([rows, columns], store);
    }

    private static DataValue ShapeFromTensor(DataValue input, IValueStore store)
    {
        input.AsTensor(store, out int[] shape);
        float[] result = new float[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            result[i] = shape[i];
        }
        return DataValue.FromVector(result, store);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        return input.Kind switch
        {
            DataKind.Vector => DataValue.FromVector([input.AsVector(store).Length], store),
            DataKind.Matrix => ShapeFromMatrix(input, store),
            DataKind.Tensor => ShapeFromTensor(input, store),
            _ => throw new InvalidOperationException($"shape() does not support {input.Kind}.")
        };
    }
}
