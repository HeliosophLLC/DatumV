using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>Element-wise sine: sin(x) in radians.</summary>
public sealed class SinFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "sin";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Sin(value);
}

/// <summary>Element-wise cosine: cos(x) in radians.</summary>
public sealed class CosFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "cos";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Cos(value);
}

/// <summary>Element-wise tangent: tan(x) in radians.</summary>
public sealed class TanFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "tan";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Tan(value);
}

/// <summary>Element-wise arc sine: asin(x) returns radians.</summary>
public sealed class AsinFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "asin";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Asin(value);
}

/// <summary>Element-wise arc cosine: acos(x) returns radians.</summary>
public sealed class AcosFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "acos";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Acos(value);
}

/// <summary>Element-wise arc tangent: atan(x) returns radians.</summary>
public sealed class AtanFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "atan";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Atan(value);
}

/// <summary>Element-wise hyperbolic sine: sinh(x).</summary>
public sealed class SinhFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "sinh";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Sinh(value);
}

/// <summary>Element-wise hyperbolic cosine: cosh(x).</summary>
public sealed class CoshFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "cosh";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Cosh(value);
}

/// <summary>Element-wise hyperbolic tangent: tanh(x).</summary>
public sealed class TanhFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "tanh";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Tanh(value);
}

/// <summary>Element-wise conversion from radians to degrees.</summary>
public sealed class DegreesFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "degrees";

    /// <inheritdoc />
    protected override float Apply(float value) => value * (180f / MathF.PI);
}

/// <summary>Element-wise conversion from degrees to radians.</summary>
public sealed class RadiansFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "radians";

    /// <inheritdoc />
    protected override float Apply(float value) => value * (MathF.PI / 180f);
}

/// <summary>Element-wise two-argument arc tangent: atan2(y, x).</summary>
public sealed class Atan2Function : BinaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "atan2";

    /// <inheritdoc />
    protected override float Apply(float a, float b) => MathF.Atan2(a, b);
}

/// <summary>Returns the constant π (pi). Takes zero arguments.</summary>
public sealed class PiFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "pi";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("pi() takes no arguments.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromFloat32(MathF.PI);
    }
}

/// <summary>Returns Euler's number e. Takes zero arguments.</summary>
public sealed class EulerFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "euler";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("euler() takes no arguments.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromFloat32(MathF.E);
    }
}
