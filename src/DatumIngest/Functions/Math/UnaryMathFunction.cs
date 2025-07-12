using DatumQuery.Model;

namespace DatumQuery.Functions.Math;

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

        if (kind is not (DataKind.Scalar or DataKind.UInt8 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"{Name}() does not support {kind}.");
        }

        return kind is DataKind.UInt8 ? DataKind.Scalar : kind;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(input.Kind is DataKind.UInt8 ? DataKind.Scalar : input.Kind);
        }

        switch (input.Kind)
        {
            case DataKind.UInt8:
                return DataValue.FromScalar(Apply(input.AsUInt8()));

            case DataKind.Scalar:
                return DataValue.FromScalar(Apply(input.AsScalar()));

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
}
