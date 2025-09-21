using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Abstract base class for unary element-wise math functions that operate on
/// Scalar, Vector, Matrix, or Tensor by applying a single <see cref="Apply(float)"/>
/// transform to each element, preserving shape.
/// </summary>
public abstract class UnaryMathFunction : IScalarFunction
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException($"{Name}() requires exactly 1 argument.");
        }

        DataKind kind = argumentKinds[0];

        if (kind is not (DataKind.Float32 or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16
            or DataKind.UInt16 or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64
            or DataKind.UInt64 or DataKind.Float64 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"{Name}() does not support {kind}.");
        }

        // All numeric scalars promote to Float32 — the computation type of Apply(float).
        return kind is DataKind.Vector or DataKind.Matrix or DataKind.Tensor ? kind : DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            // All numeric scalars null-propagate as Float32 — consistent with ValidateArguments.
            return DataValue.Null(
                input.Kind is DataKind.Vector or DataKind.Matrix or DataKind.Tensor
                    ? input.Kind
                    : DataKind.Float32);
        }

        switch (input.Kind)
        {
            case DataKind.UInt8:
            case DataKind.Int8:
            case DataKind.Int16:
            case DataKind.UInt16:
            case DataKind.Int32:
            case DataKind.UInt32:
            case DataKind.Int64:
            case DataKind.UInt64:
            case DataKind.Float32:
            case DataKind.Float64:
                return DataValue.FromFloat32(Apply(ExtractFloat(input)));

            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = Apply(source[i]);
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = Apply(source[i]);
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = Apply(source[i]);
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"{Name}() does not support {input.Kind}.");
        }
    }

    /// <summary>
    /// Applies the math function to a single float element.
    /// </summary>
    protected abstract float Apply(float value);

    private static float ExtractFloat(DataValue value) => value.ToFloat();
}
