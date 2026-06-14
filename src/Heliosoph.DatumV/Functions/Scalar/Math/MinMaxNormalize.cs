using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// <c>min_max_normalize(value) → FLOAT32</c> /
/// <c>min_max_normalize(value, min, max) → FLOAT32</c>.
/// Normalizes a numeric value into <c>[0, 1]</c> via
/// <c>(value - min) / (max - min)</c>. The single-argument form derives the
/// range from the integer input's kind (e.g. <c>UInt8</c> uses
/// <c>0..255</c>). Floating-point inputs require explicit bounds. Array
/// overloads apply the transform element-wise, returning a flat
/// <c>FLOAT32[]</c>. Null value propagates to a typed null.
/// </summary>
/// <remarks>
/// Throws when <c>min &gt;= max</c>, when either bound is NaN, or when the
/// single-argument form is called with a kind outside
/// <c>{Int8, Int16, Int32, Int64, UInt8, UInt16, UInt32, UInt64}</c>.
/// </remarks>
public sealed class MinMaxNormalizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "min_max_normalize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Min/max normalizes a numeric value or vector into [0, 1]. " +
        "Integer single-arg form uses the kind's natural range as bounds; " +
        "explicit min/max may be supplied as the second and third arguments.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("min",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Array),
                new ParameterSpec("min",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Family(DataKindFamily.NumericScalar), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MinMaxNormalizeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];

        double min, max;
        if (args.Length == 1)
        {
            if (!TryDefaultRange(value.Kind, out min, out max))
            {
                throw new FunctionArgumentException(Name,
                    $"single-argument form requires an integer kind; got {value.Kind}. " +
                    "Pass explicit min and max for Float / Decimal inputs.");
            }
        }
        else
        {
            if (args[1].IsNull || args[2].IsNull)
            {
                return new(value.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
            }
            args[1].TryToDouble(out min);
            args[2].TryToDouble(out max);
        }

        if (double.IsNaN(min) || double.IsNaN(max) || min >= max)
        {
            throw new FunctionArgumentException(Name,
                $"requires min < max with both finite; got min={min}, max={max}.");
        }

        double range = max - min;
        if (!value.IsArray)
        {
            if (value.IsNull) return new(ValueRef.Null(DataKind.Float32));
            value.TryToDouble(out double v);
            return new(ValueRef.FromFloat32((float)((v - min) / range)));
        }

        if (value.IsNull) return new(ValueRef.NullArray(DataKind.Float32));
        float[] result = NormalizeArray(value, min, range);
        return new(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }

    private static float[] NormalizeArray(ValueRef arrayArg, double min, double range)
    {
        switch (arrayArg.Materialized)
        {
            case byte[] u8:    return Map(u8, min, range);
            case sbyte[] i8:   return Map(i8, min, range);
            case ushort[] u16: return Map(u16, min, range);
            case short[] i16:  return Map(i16, min, range);
            case uint[] u32:   return Map(u32, min, range);
            case int[] i32:    return Map(i32, min, range);
            case ulong[] u64:  return Map(u64, min, range);
            case long[] i64:   return Map(i64, min, range);
            case float[] f32:  return Map(f32, min, range);
            case double[] f64: return Map(f64, min, range);
        }

        ReadOnlySpan<ValueRef> elements = arrayArg.GetArrayElements();
        float[] result = new float[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToDouble(out double v))
            {
                throw new FunctionArgumentException(Name,
                    $"could not coerce array element [{i}] of kind {elements[i].Kind} to a numeric value.");
            }
            result[i] = (float)((v - min) / range);
        }
        return result;
    }

    private static float[] Map<T>(T[] src, double min, double range)
        where T : struct, IConvertible
    {
        float[] dst = new float[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            double v = Convert.ToDouble(src[i]);
            dst[i] = (float)((v - min) / range);
        }
        return dst;
    }

    private static bool TryDefaultRange(DataKind kind, out double min, out double max)
    {
        switch (kind)
        {
            case DataKind.Int8:   min = sbyte.MinValue;  max = sbyte.MaxValue;  return true;
            case DataKind.Int16:  min = short.MinValue;  max = short.MaxValue;  return true;
            case DataKind.Int32:  min = int.MinValue;    max = int.MaxValue;    return true;
            case DataKind.Int64:  min = long.MinValue;   max = long.MaxValue;   return true;
            case DataKind.UInt8:  min = 0;               max = byte.MaxValue;   return true;
            case DataKind.UInt16: min = 0;               max = ushort.MaxValue; return true;
            case DataKind.UInt32: min = 0;               max = uint.MaxValue;   return true;
            case DataKind.UInt64: min = 0;               max = ulong.MaxValue;  return true;
            default:              min = 0;               max = 0;               return false;
        }
    }
}

/// <summary>
/// <c>denormalize(value FLOAT32, min FLOAT32, max FLOAT32) → FLOAT32</c> /
/// <c>denormalize(value FLOAT32[], min FLOAT32, max FLOAT32) → FLOAT32[]</c>.
/// Full inverse of <see cref="MinMaxNormalizeFunction"/>: maps a normalized
/// <c>[0, 1]</c> value back to its original range via
/// <c>value * (max - min) + min</c>. Array overloads apply the transform
/// element-wise. Null value propagates to a typed null.
/// </summary>
/// <remarks>
/// Throws when <c>min &gt;= max</c> or either bound is NaN, matching
/// <see cref="MinMaxNormalizeFunction"/>'s preconditions.
/// </remarks>
public sealed class DenormalizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "denormalize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Inverse of min_max_normalize: maps a normalized [0, 1] Float32 scalar or vector " +
        "back to its original range via value * (max - min) + min.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("min",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("min",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DenormalizeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];

        if (args[1].IsNull || args[2].IsNull)
        {
            return new(value.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
        }

        float min = args[1].AsFloat32();
        float max = args[2].AsFloat32();
        if (float.IsNaN(min) || float.IsNaN(max) || min >= max)
        {
            throw new FunctionArgumentException(Name,
                $"requires min < max with both finite; got min={min}, max={max}.");
        }

        float range = max - min;
        return new(ActivationOps.Apply(value, frame, x => x * range + min));
    }
}
