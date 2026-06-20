using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Math;

/// <summary>
/// Returns the cube root of a numeric input as <see cref="DataKind.Float64"/>.
/// Negative inputs return the real cube root (e.g. <c>cbrt(-8) = -2</c>), matching
/// <see cref="System.Math.Cbrt"/>. Null input propagates to null output.
/// </summary>
public sealed class CbrtFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cbrt";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the cube root of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CbrtFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Cbrt(v)));
    }
}

/// <summary>
/// Returns the square (x²) of a numeric input. The result kind matches the
/// input kind; integer kinds wrap on overflow, matching native multiplication.
/// Null input propagates to null output.
/// </summary>
public sealed class SquareFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "square";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the square (x²) of a numeric input. Result kind matches the input kind.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(
                    "value",
                    DataKindMatcher.OneOf(
                        DataKind.Int8, DataKind.Int16, DataKind.Int32, DataKind.Int64, DataKind.Int128,
                        DataKind.UInt8, DataKind.UInt16, DataKind.UInt32, DataKind.UInt64, DataKind.UInt128,
                        DataKind.Float16, DataKind.Float32, DataKind.Float64, DataKind.Decimal)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SquareFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(input.Kind));

        ValueRef result = input.Kind switch
        {
            DataKind.Int8 => ValueRef.FromInt8((sbyte)(input.AsInt8() * input.AsInt8())),
            DataKind.Int16 => ValueRef.FromInt16((short)(input.AsInt16() * input.AsInt16())),
            DataKind.Int32 => ValueRef.FromInt32(unchecked(input.AsInt32() * input.AsInt32())),
            DataKind.Int64 => ValueRef.FromInt64(unchecked(input.AsInt64() * input.AsInt64())),
            DataKind.Int128 => ValueRef.FromInt128(unchecked(input.AsInt128() * input.AsInt128())),
            DataKind.UInt8 => ValueRef.FromUInt8((byte)(input.AsUInt8() * input.AsUInt8())),
            DataKind.UInt16 => ValueRef.FromUInt16((ushort)(input.AsUInt16() * input.AsUInt16())),
            DataKind.UInt32 => ValueRef.FromUInt32(unchecked(input.AsUInt32() * input.AsUInt32())),
            DataKind.UInt64 => ValueRef.FromUInt64(unchecked(input.AsUInt64() * input.AsUInt64())),
            DataKind.UInt128 => ValueRef.FromUInt128(unchecked(input.AsUInt128() * input.AsUInt128())),
            DataKind.Float16 => ValueRef.FromFloat16((Half)((float)input.AsFloat16() * (float)input.AsFloat16())),
            DataKind.Float32 => ValueRef.FromFloat32(input.AsFloat32() * input.AsFloat32()),
            DataKind.Float64 => ValueRef.FromFloat64(input.AsFloat64() * input.AsFloat64()),
            DataKind.Decimal => ValueRef.FromDecimal(input.AsDecimal() * input.AsDecimal()),

            _ => throw new FunctionArgumentException(Name, $"does not support kind {input.Kind}."),
        };
        return new ValueTask<ValueRef>(result);
    }
}

/// <summary>
/// Returns the natural exponential (eˣ) of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class ExpFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "exp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the natural exponential (eˣ) of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ExpFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Exp(v)));
    }
}

/// <summary>
/// Returns the base-2 exponential (2ˣ) of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class Exp2Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "exp2";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the base-2 exponential (2ˣ) of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Exp2Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Pow(2.0, v)));
    }
}

/// <summary>
/// Returns the natural logarithm (ln) of a numeric input as
/// <see cref="DataKind.Float64"/>. Non-positive inputs surface as
/// <see cref="double.NaN"/> or <see cref="double.NegativeInfinity"/>, matching
/// <see cref="System.Math.Log(double)"/>. Null input propagates to null output.
/// </summary>
public sealed class LnFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "ln";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the natural logarithm of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LnFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Log(v)));
    }
}

/// <summary>
/// Returns the base-2 logarithm of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class Log2Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "log2";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the base-2 logarithm of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Log2Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Log2(v)));
    }
}

/// <summary>
/// Returns the base-10 logarithm of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class Log10Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "log10";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the base-10 logarithm of a numeric input as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Log10Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        input.TryToDouble(out double v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Log10(v)));
    }
}

/// <summary>
/// Returns <c>base</c> raised to the power of <c>exponent</c>, as
/// <see cref="DataKind.Float64"/>. A null in either argument propagates to a
/// null result. Matches <see cref="System.Math.Pow"/> for edge cases.
/// </summary>
public sealed class PowFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pow";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns base raised to the power of exponent, as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("base", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("exponent", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PowFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef baseValue = args[0];
        ValueRef exponent = args[1];
        if (baseValue.IsNull || exponent.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        baseValue.TryToDouble(out double b);
        exponent.TryToDouble(out double e);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Pow(b, e)));
    }
}

/// <summary>
/// Returns the logarithm of <c>value</c> in the given <c>base</c>, as
/// <see cref="DataKind.Float64"/>. A null in either argument propagates to a
/// null result. Matches <see cref="System.Math.Log(double, double)"/>.
/// </summary>
public sealed class LogFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "log";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Returns the logarithm of value in the given base, as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("base", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LogFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef baseValue = args[1];
        if (value.IsNull || baseValue.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        value.TryToDouble(out double v);
        baseValue.TryToDouble(out double b);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Log(v, b)));
    }
}
