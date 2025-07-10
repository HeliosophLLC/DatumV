namespace Axon.QueryEngine.Functions.Math;

/// <summary>Element-wise absolute value: abs(x) = |x|.</summary>
public sealed class AbsFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "abs";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Abs(value);
}

/// <summary>Element-wise sign: sign(x) returns -1, 0, or 1.</summary>
public sealed class SignFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "sign";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Sign(value);
}

/// <summary>Element-wise negation: negate(x) = -x.</summary>
public sealed class NegateFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "negate";

    /// <inheritdoc />
    protected override float Apply(float value) => -value;
}

/// <summary>Element-wise modulus: mod(a, b) = a % b.</summary>
public sealed class ModFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "mod";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => a % b;
}

/// <summary>Element-wise addition: add(a, b) = a + b.</summary>
public sealed class AddFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "add";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => a + b;
}

/// <summary>Element-wise subtraction: subtract(a, b) = a - b.</summary>
public sealed class SubtractFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "subtract";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => a - b;
}

/// <summary>Element-wise multiplication: multiply(a, b) = a * b.</summary>
public sealed class MultiplyFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "multiply";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => a * b;
}

/// <summary>Element-wise division: divide(a, b) = a / b.</summary>
public sealed class DivideFunction : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "divide";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => a / b;
}
