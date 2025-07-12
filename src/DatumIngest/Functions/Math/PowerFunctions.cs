namespace DatumIngest.Functions.Math;

/// <summary>Element-wise square root: sqrt(x) = √x.</summary>
public sealed class SqrtFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "sqrt";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Sqrt(value);
}

/// <summary>Element-wise cube root: cbrt(x) = x^(1/3).</summary>
public sealed class CbrtFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "cbrt";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Cbrt(value);
}

/// <summary>Element-wise square: square(x) = x².</summary>
public sealed class SquareFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "square";

    /// <inheritdoc />
    protected override float Apply(float value) => value * value;
}

/// <summary>Element-wise natural exponential: exp(x) = eˣ.</summary>
public sealed class ExpFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "exp";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Exp(value);
}

/// <summary>Element-wise base-2 exponential: exp2(x) = 2ˣ.</summary>
public sealed class Exp2Function : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "exp2";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Pow(2f, value);
}

/// <summary>Element-wise natural logarithm: ln(x) = logₑ(x).</summary>
public sealed class LnFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "ln";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Log(value);
}

/// <summary>Element-wise base-2 logarithm: log2(x).</summary>
public sealed class Log2Function : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "log2";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Log2(value);
}

/// <summary>Element-wise base-10 logarithm: log10(x).</summary>
public sealed class Log10Function : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "log10";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Log10(value);
}

/// <summary>Element-wise power: pow(base, exponent).</summary>
public sealed class PowFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "pow";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => MathF.Pow(a, b);
}

/// <summary>Element-wise logarithm with custom base: log(x, base).</summary>
public sealed class LogFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "log";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => MathF.Log(a, b);
}
