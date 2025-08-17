using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>Element-wise ceiling: ceil(x) rounds up to nearest integer.</summary>
public sealed class CeilFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "ceil";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Ceiling(value);
}

/// <summary>Element-wise floor: floor(x) rounds down to nearest integer.</summary>
public sealed class FloorFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "floor";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Floor(value);
}

/// <summary>Element-wise truncation: truncate(x) removes fractional part toward zero.</summary>
public sealed class TruncateFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "truncate";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Truncate(value);
}

/// <summary>
/// Rounds a value to a specified number of decimal places: round(x) or round(x, decimals).
/// When called with one argument, rounds to nearest integer. When called with two, rounds
/// to the specified number of decimal places.
/// </summary>
public sealed class RoundFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "round";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("round() requires 1 or 2 arguments.");
        }

        DataKind kind = argumentKinds[0];
        if (kind is not (DataKind.Float32 or DataKind.UInt8 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"round() does not support {kind}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException("round() second argument must be Scalar or UInt8.");
        }

        return kind is DataKind.UInt8 ? DataKind.Float32 : kind;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(input.Kind is DataKind.UInt8 ? DataKind.Float32 : input.Kind);
        }

        int decimals = 0;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            decimals = (int)(arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsFloat32());
        }

        float Round(float v) => MathF.Round(v, decimals, MidpointRounding.AwayFromZero);

        switch (input.Kind)
        {
            case DataKind.UInt8:
                return DataValue.FromFloat32(Round(input.AsUInt8()));
            case DataKind.Float32:
                return DataValue.FromFloat32(Round(input.AsFloat32()));
            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Round(source[i]);
                return DataValue.FromVector(result);
            }
            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Round(source[i]);
                return DataValue.FromMatrix(result, rows, columns);
            }
            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Round(source[i]);
                return DataValue.FromTensor(result, shape);
            }
            default:
                throw new InvalidOperationException($"round() does not support {input.Kind}.");
        }
    }
}

/// <summary>
/// Quantizes a value to the nearest multiple of a step: quantize(x, step).
/// Example: quantize(3.7, 0.5) = 3.5.
/// </summary>
public sealed class QuantizeFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "quantize";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => b == 0f ? a : MathF.Round(a / b) * b;
}

/// <summary>
/// Assigns a value to a bucket index based on boundary thresholds: bucketize(value, boundaries_vector).
/// The boundaries vector must be sorted in ascending order. Returns the index of the bucket
/// the value falls into (0-based). For n boundaries, there are n+1 buckets.
/// </summary>
public sealed class BucketizeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "bucketize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("bucketize() requires exactly 2 arguments.");
        }

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException("bucketize() first argument must be Scalar or UInt8.");
        }

        if (argumentKinds[1] is not DataKind.Vector)
        {
            throw new ArgumentException("bucketize() second argument must be a Vector of boundaries.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataValue boundaries = arguments[1];

        if (input.IsNull || boundaries.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        float value = input.Kind is DataKind.UInt8 ? input.AsUInt8() : input.AsFloat32();
        float[] bounds = boundaries.AsVector();

        int bucket = 0;
        for (int i = 0; i < bounds.Length; i++)
        {
            if (value >= bounds[i])
            {
                bucket = i + 1;
            }
            else
            {
                break;
            }
        }

        return DataValue.FromFloat32(bucket);
    }
}

/// <summary>
/// Clips a value to a range: clip(x, min, max). Alias for clamp().
/// </summary>
public sealed class ClipFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "clip";

    private readonly Scalar.ClampFunction _clamp = new();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) => _clamp.ValidateArguments(argumentKinds);

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) => _clamp.Execute(arguments);
}
