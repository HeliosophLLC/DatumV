using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Extracts a slice from a vector: vec_slice(vector, start, length).
/// Returns a new Vector of the specified length starting at the given index.
/// </summary>
public sealed class VecSliceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_slice";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("vec_slice() requires exactly 3 arguments (vector, start, length).");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_slice() first argument must be a Vector.");
        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException("vec_slice() second argument (start) must be Scalar or UInt8.");
        if (argumentKinds[2] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException("vec_slice() third argument (length) must be Scalar or UInt8.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        int start = (int)(arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32());
        int length = (int)(arguments[2].Kind is DataKind.UInt8 ? arguments[2].AsUInt8() : arguments[2].AsFloat32());

        start = System.Math.Clamp(start, 0, source.Length);
        length = System.Math.Clamp(length, 0, source.Length - start);

        float[] result = new float[length];
        Array.Copy(source, start, result, 0, length);
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Constructs a vector from one or more scalar or vector arguments: vec(a, b, ...).
/// Scalars contribute a single element; vectors are flattened in order.
/// </summary>
public sealed class VecFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1)
            throw new ArgumentException("vec() requires at least 1 argument.");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] is not (DataKind.Float32 or DataKind.UInt8 or DataKind.Vector))
                throw new ArgumentException($"vec() argument {i + 1} must be Scalar, UInt8, or Vector.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int totalLength = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) return DataValue.Null(DataKind.Vector);
            totalLength += arguments[i].Kind is DataKind.Vector ? arguments[i].AsVector().Length : 1;
        }

        float[] result = new float[totalLength];
        int offset = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].Kind is DataKind.Vector)
            {
                float[] source = arguments[i].AsVector();
                Array.Copy(source, 0, result, offset, source.Length);
                offset += source.Length;
            }
            else
            {
                result[offset++] = arguments[i].Kind is DataKind.UInt8
                    ? arguments[i].AsUInt8()
                    : arguments[i].AsFloat32();
            }
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Stacks two or more equal-length vectors as rows into a Matrix: tensor(v1, v2, ...).
/// Each vector becomes one row. All vectors must have the same length.
/// </summary>
public sealed class TensorFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "tensor";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("tensor() requires at least 2 arguments.");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] is not DataKind.Vector)
                throw new ArgumentException($"tensor() argument {i + 1} must be a Vector.");
        }
        return DataKind.Matrix;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) return DataValue.Null(DataKind.Matrix);
        }

        int columns = arguments[0].AsVector().Length;
        for (int i = 1; i < arguments.Length; i++)
        {
            if (arguments[i].AsVector().Length != columns)
                throw new ArgumentException($"tensor() all vectors must have the same length. Vector 1 has {columns} elements but vector {i + 1} has {arguments[i].AsVector().Length}.");
        }

        int rows = arguments.Length;
        float[] result = new float[rows * columns];
        for (int i = 0; i < rows; i++)
        {
            Array.Copy(arguments[i].AsVector(), 0, result, i * columns, columns);
        }

        return DataValue.FromMatrix(result, rows, columns);
    }
}

/// <summary>
/// Concatenates two or more vectors: vec_concat(v1, v2, ...).
/// </summary>
public sealed class VecConcatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_concat";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("vec_concat() requires at least 2 arguments.");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] is not DataKind.Vector)
                throw new ArgumentException($"vec_concat() argument {i + 1} must be a Vector.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        int totalLength = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) return DataValue.Null(DataKind.Vector);
            totalLength += arguments[i].AsVector().Length;
        }

        float[] result = new float[totalLength];
        int offset = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            float[] source = arguments[i].AsVector();
            Array.Copy(source, 0, result, offset, source.Length);
            offset += source.Length;
        }

        return DataValue.FromVector(result);
    }
}

/// <summary>Reverses the elements of a vector: vec_reverse(vector).</summary>
public sealed class VecReverseFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_reverse";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_reverse() requires exactly 1 argument.");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_reverse() requires a Vector argument.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        float[] result = new float[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[source.Length - 1 - i];
        }
        return DataValue.FromVector(result);
    }
}

/// <summary>Returns a sorted copy of a vector in ascending order: vec_sort(vector).</summary>
public sealed class VecSortFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_sort";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_sort() requires exactly 1 argument.");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_sort() requires a Vector argument.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        float[] result = new float[source.Length];
        Array.Copy(source, result, source.Length);
        Array.Sort(result);
        return DataValue.FromVector(result);
    }
}

/// <summary>Returns unique elements of a vector (preserving first occurrence order): vec_unique(vector).</summary>
public sealed class VecUniqueFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_unique";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_unique() requires exactly 1 argument.");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_unique() requires a Vector argument.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        HashSet<float> seen = new();
        List<float> unique = new();
        for (int i = 0; i < source.Length; i++)
        {
            if (seen.Add(source[i]))
            {
                unique.Add(source[i]);
            }
        }
        return DataValue.FromVector(unique.ToArray());
    }
}

/// <summary>Flattens a Matrix or Tensor to a Vector: vec_flatten(input).</summary>
public sealed class VecFlattenFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_flatten";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("vec_flatten() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
            throw new ArgumentException($"vec_flatten() does not support {argumentKinds[0]}.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] data = arguments[0].Kind switch
        {
            DataKind.Vector => arguments[0].AsVector(),
            DataKind.Matrix => arguments[0].AsMatrix(out _, out _),
            DataKind.Tensor => arguments[0].AsTensor(out _),
            _ => []
        };
        float[] result = new float[data.Length];
        Array.Copy(data, result, data.Length);
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Pads a vector to a target length with a fill value: vec_pad(vector, targetLength, fillValue).
/// If the vector is already at or longer than targetLength, it is returned unchanged.
/// </summary>
public sealed class VecPadFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_pad";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("vec_pad() requires exactly 3 arguments (vector, targetLength, fillValue).");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_pad() first argument must be a Vector.");
        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException("vec_pad() second argument (targetLength) must be Scalar or UInt8.");
        if (argumentKinds[2] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException("vec_pad() third argument (fillValue) must be Scalar or UInt8.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        int targetLength = (int)(arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32());
        float fillValue = arguments[2].Kind is DataKind.UInt8 ? arguments[2].AsUInt8() : arguments[2].AsFloat32();

        if (source.Length >= targetLength)
        {
            float[] copy = new float[source.Length];
            Array.Copy(source, copy, source.Length);
            return DataValue.FromVector(copy);
        }

        float[] result = new float[targetLength];
        Array.Copy(source, result, source.Length);
        for (int i = source.Length; i < targetLength; i++)
        {
            result[i] = fillValue;
        }
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Repeats a vector n times: vec_repeat(vector, count).
/// </summary>
public sealed class VecRepeatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "vec_repeat";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("vec_repeat() requires exactly 2 arguments (vector, count).");
        if (argumentKinds[0] is not DataKind.Vector)
            throw new ArgumentException("vec_repeat() first argument must be a Vector.");
        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
            throw new ArgumentException("vec_repeat() second argument (count) must be Scalar or UInt8.");
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Vector);
        float[] source = arguments[0].AsVector();
        int count = (int)(arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32());
        if (count <= 0) return DataValue.FromVector([]);

        float[] result = new float[source.Length * count];
        for (int c = 0; c < count; c++)
        {
            Array.Copy(source, 0, result, c * source.Length, source.Length);
        }
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Generates a vector of evenly spaced values: linspace(start, stop, count).
/// </summary>
public sealed class LinspaceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "linspace";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("linspace() requires exactly 3 arguments (start, stop, count).");
        for (int i = 0; i < 3; i++)
        {
            if (argumentKinds[i] is not (DataKind.Float32 or DataKind.UInt8))
                throw new ArgumentException($"linspace() argument {i + 1} must be Scalar or UInt8.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float start = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsFloat32();
        float stop = arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32();
        int count = (int)(arguments[2].Kind is DataKind.UInt8 ? arguments[2].AsUInt8() : arguments[2].AsFloat32());

        if (count <= 0) return DataValue.FromVector([]);
        if (count == 1) return DataValue.FromVector([start]);

        float[] result = new float[count];
        float step = (stop - start) / (count - 1);
        for (int i = 0; i < count; i++)
        {
            result[i] = start + step * i;
        }
        result[count - 1] = stop; // Ensure exact endpoint
        return DataValue.FromVector(result);
    }
}

/// <summary>
/// Generates a vector of values with a fixed step: arange(start, stop, step).
/// Similar to Python's numpy.arange.
/// </summary>
public sealed class ArangeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "arange";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("arange() requires exactly 3 arguments (start, stop, step).");
        for (int i = 0; i < 3; i++)
        {
            if (argumentKinds[i] is not (DataKind.Float32 or DataKind.UInt8))
                throw new ArgumentException($"arange() argument {i + 1} must be Scalar or UInt8.");
        }
        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float start = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsFloat32();
        float stop = arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32();
        float step = arguments[2].Kind is DataKind.UInt8 ? arguments[2].AsUInt8() : arguments[2].AsFloat32();

        if (step == 0f) throw new ArgumentException("arange() step cannot be zero.");

        List<float> values = new();
        if (step > 0)
        {
            for (float v = start; v < stop; v += step)
            {
                values.Add(v);
            }
        }
        else
        {
            for (float v = start; v > stop; v += step)
            {
                values.Add(v);
            }
        }

        return DataValue.FromVector(values.ToArray());
    }
}
