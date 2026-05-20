using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Trigonometry;

/// <summary>
/// Returns the sine of a numeric input, interpreted as radians, as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class SinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sin";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the sine of a numeric input, interpreted as radians, as Float64.";

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
        FunctionMetadata.Validate<SinFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Sin(v)));
    }
}

/// <summary>
/// Returns the cosine of a numeric input, interpreted as radians, as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class CosFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cos";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the cosine of a numeric input, interpreted as radians, as Float64.";

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
        FunctionMetadata.Validate<CosFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Cos(v)));
    }
}

/// <summary>
/// Returns the tangent of a numeric input, interpreted as radians, as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class TanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "tan";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the tangent of a numeric input, interpreted as radians, as Float64.";

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
        FunctionMetadata.Validate<TanFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Tan(v)));
    }
}

/// <summary>
/// Returns the cotangent of a numeric input, interpreted as radians, as
/// <see cref="DataKind.Float64"/>. Equivalent to <c>1 / tan(x)</c>. Null
/// input propagates to null output. At inputs where the tangent is zero
/// (integer multiples of π), the result surfaces as ±∞ from the underlying
/// floating-point division, matching PostgreSQL's behavior.
/// </summary>
public sealed class CotFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cot";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the cotangent of a numeric input, interpreted as radians, as Float64.";

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
        FunctionMetadata.Validate<CotFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(1.0 / System.Math.Tan(v)));
    }
}

/// <summary>
/// Returns the arc sine of a numeric input, in radians, as
/// <see cref="DataKind.Float64"/>. Inputs outside [-1, 1] surface as
/// <see cref="double.NaN"/>, matching <see cref="System.Math.Asin"/>. Null
/// input propagates to null output.
/// </summary>
public sealed class AsinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "asin";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the arc sine of a numeric input, in radians, as Float64.";

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
        FunctionMetadata.Validate<AsinFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Asin(v)));
    }
}

/// <summary>
/// Returns the arc cosine of a numeric input, in radians, as
/// <see cref="DataKind.Float64"/>. Inputs outside [-1, 1] surface as
/// <see cref="double.NaN"/>, matching <see cref="System.Math.Acos"/>. Null
/// input propagates to null output.
/// </summary>
public sealed class AcosFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "acos";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the arc cosine of a numeric input, in radians, as Float64.";

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
        FunctionMetadata.Validate<AcosFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Acos(v)));
    }
}

/// <summary>
/// Returns the arc tangent of a numeric input, in radians, as
/// <see cref="DataKind.Float64"/>. Result range is (-π/2, π/2). Null input
/// propagates to null output.
/// </summary>
public sealed class AtanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "atan";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the arc tangent of a numeric input, in radians, as Float64.";

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
        FunctionMetadata.Validate<AtanFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Atan(v)));
    }
}

/// <summary>
/// Returns the angle in radians between the positive x-axis and the ray to
/// the point <c>(x, y)</c>, as <see cref="DataKind.Float64"/>. Result range
/// is [-π, π]. A null in either argument propagates to a null result.
/// </summary>
public sealed class Atan2Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "atan2";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the angle in radians between the positive x-axis and the ray to (x, y), as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<Atan2Function>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef y = args[0];
        ValueRef x = args[1];
        if (y.IsNull || x.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float64));

        y.TryToDouble(out double yv);
        x.TryToDouble(out double xv);
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Atan2(yv, xv)));
    }
}

/// <summary>
/// Returns the mathematical constant π as <see cref="DataKind.Float64"/>.
/// </summary>
public sealed class PiFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pi";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description => "Returns the constant π as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PiFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ValueRef.FromFloat64(System.Math.PI));

    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// Returns Euler's number e as <see cref="DataKind.Float64"/>.
/// </summary>
public sealed class EulerFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "euler";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description => "Returns Euler's number e as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<EulerFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ValueRef.FromFloat64(System.Math.E));

    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// Returns the hyperbolic sine of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class SinhFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sinh";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the hyperbolic sine of a numeric input as Float64.";

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
        FunctionMetadata.Validate<SinhFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Sinh(v)));
    }
}

/// <summary>
/// Returns the hyperbolic cosine of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class CoshFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cosh";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the hyperbolic cosine of a numeric input as Float64.";

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
        FunctionMetadata.Validate<CoshFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Cosh(v)));
    }
}

/// <summary>
/// Returns the hyperbolic tangent of a numeric input as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class TanhFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "tanh";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Returns the hyperbolic tangent of a numeric input as Float64.";

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
        FunctionMetadata.Validate<TanhFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(System.Math.Tanh(v)));
    }
}

/// <summary>
/// Converts a numeric input from radians to degrees, returned as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class DegreesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "degrees";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Converts a numeric input from radians to degrees, as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("radians", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DegreesFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(v * (180.0 / System.Math.PI)));
    }
}

/// <summary>
/// Converts a numeric input from degrees to radians, returned as
/// <see cref="DataKind.Float64"/>. Null input propagates to null output.
/// </summary>
public sealed class RadiansFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "radians";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Trigonometry;

    /// <inheritdoc />
    public static string Description =>
        "Converts a numeric input from degrees to radians, as Float64.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("degrees", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RadiansFunction>(argumentKinds);

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
        return new ValueTask<ValueRef>(ValueRef.FromFloat64(v * (System.Math.PI / 180.0)));
    }
}
