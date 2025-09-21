using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Abstract base class for binary element-wise math functions that take two numeric
/// arguments. Both arguments can be Scalar, Vector, Matrix, or Tensor. When one
/// argument is Scalar and the other is a higher-rank type, the scalar is broadcast.
/// When both are the same rank, element-wise operation is performed.
/// </summary>
public abstract class BinaryMathFunction : IScalarFunction
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly 2 arguments.");
        }

        DataKind kindA = argumentKinds[0];
        DataKind kindB = argumentKinds[1];

        if (kindA is not (DataKind.Float32 or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16
            or DataKind.UInt16 or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64
            or DataKind.UInt64 or DataKind.Float64 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"{Name}() does not support {kindA} as first argument.");
        }

        if (kindB is not (DataKind.Float32 or DataKind.UInt8 or DataKind.Int8 or DataKind.Int16
            or DataKind.UInt16 or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64
            or DataKind.UInt64 or DataKind.Float64 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"{Name}() does not support {kindB} as second argument.");
        }

        // All numeric scalars promote to Float32 — the computation type of Apply(float, float).
        // Higher-rank types (Vector, Matrix, Tensor) always win over scalars.
        bool aIsArray = kindA is DataKind.Vector or DataKind.Matrix or DataKind.Tensor;
        bool bIsArray = kindB is DataKind.Vector or DataKind.Matrix or DataKind.Tensor;

        if (aIsArray && bIsArray)
        {
            // Return the higher-rank of the two (DataKind enum order: Vector < Matrix < Tensor).
            return kindA >= kindB ? kindA : kindB;
        }

        if (aIsArray) return kindA;
        if (bIsArray) return kindB;

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue inputA = arguments[0];
        DataValue inputB = arguments[1];

        if (inputA.IsNull || inputB.IsNull)
        {
            DataKind resultKind = ValidateArguments([inputA.Kind, inputB.Kind]);
            return DataValue.Null(resultKind);
        }

        // Extract scalar values for broadcast cases
        bool aIsScalar = IsNumericScalar(inputA.Kind);
        bool bIsScalar = IsNumericScalar(inputB.Kind);

        if (aIsScalar && bIsScalar)
        {
            float a = ExtractFloat(inputA);
            float b = ExtractFloat(inputB);
            return DataValue.FromFloat32(Apply(a, b));
        }

        // At least one is array-like
        return (aIsScalar, bIsScalar) switch
        {
            (true, false) => ApplyScalarLeft(inputA, inputB),
            (false, true) => ApplyScalarRight(inputA, inputB),
            _ => ApplyBoth(inputA, inputB)
        };
    }

    /// <summary>
    /// Applies the binary operation to two float values.
    /// </summary>
    protected abstract float Apply(float a, float b);

    private DataValue ApplyScalarLeft(DataValue scalarVal, DataValue arrayVal)
    {
        float scalar = ExtractFloat(scalarVal);
        return MapArray(arrayVal, element => Apply(scalar, element));
    }

    private DataValue ApplyScalarRight(DataValue arrayVal, DataValue scalarVal)
    {
        float scalar = ExtractFloat(scalarVal);
        return MapArray(arrayVal, element => Apply(element, scalar));
    }

    private DataValue ApplyBoth(DataValue a, DataValue b)
    {
        float[] sourceA = ExtractFloats(a, out int[] shapeA);
        float[] sourceB = ExtractFloats(b, out _);

        int length = System.Math.Min(sourceA.Length, sourceB.Length);
        float[] result = new float[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = Apply(sourceA[i], sourceB[i]);
        }

        return ReconstructFromShape(result, a.Kind, shapeA);
    }

    private DataValue MapArray(DataValue arrayVal, Func<float, float> transform)
    {
        switch (arrayVal.Kind)
        {
            case DataKind.Vector:
            {
                float[] source = arrayVal.AsVector();
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = transform(source[i]);
                }
                return DataValue.FromVector(result);
            }

            case DataKind.Matrix:
            {
                float[] source = arrayVal.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = transform(source[i]);
                }
                return DataValue.FromMatrix(result, rows, columns);
            }

            case DataKind.Tensor:
            {
                float[] source = arrayVal.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    result[i] = transform(source[i]);
                }
                return DataValue.FromTensor(result, shape);
            }

            default:
                throw new InvalidOperationException($"{Name}() does not support {arrayVal.Kind}.");
        }
    }

    private static float[] ExtractFloats(DataValue value, out int[] shape)
    {
        switch (value.Kind)
        {
            case DataKind.Vector:
            {
                float[] data = value.AsVector();
                shape = [data.Length];
                return data;
            }
            case DataKind.Matrix:
            {
                float[] data = value.AsMatrix(out int rows, out int cols);
                shape = [rows, cols];
                return data;
            }
            case DataKind.Tensor:
            {
                float[] data = value.AsTensor(out shape);
                return data;
            }
            default:
                shape = [];
                return [];
        }
    }

    private static DataValue ReconstructFromShape(float[] data, DataKind kind, int[] shape)
    {
        return kind switch
        {
            DataKind.Vector => DataValue.FromVector(data),
            DataKind.Matrix => DataValue.FromMatrix(data, shape[0], shape[1]),
            DataKind.Tensor => DataValue.FromTensor(data, shape),
            _ => DataValue.FromVector(data)
        };
    }

    /// <summary>
    /// Returns true when <paramref name="kind"/> is any numeric scalar kind.
    /// </summary>
    private static bool IsNumericScalar(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64 or DataKind.UInt8;

    private static float ExtractFloat(DataValue value) => value.ToFloat();
}
