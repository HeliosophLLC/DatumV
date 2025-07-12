using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>Element-wise sigmoid activation: σ(x) = 1 / (1 + e^(-x)).</summary>
public sealed class SigmoidFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "sigmoid";

    /// <inheritdoc />
    protected override float Apply(float value) => 1f / (1f + MathF.Exp(-value));
}

/// <summary>Element-wise ReLU activation: relu(x) = max(0, x).</summary>
public sealed class ReluFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "relu";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Max(0f, value);
}

/// <summary>Element-wise SELU activation: selu(x) = λ * (x if x > 0, else α * (eˣ - 1)).</summary>
public sealed class SeluFunction : UnaryMathFunction
{
    private const float Alpha = 1.6732632423543772f;
    private const float Lambda = 1.0507009873554805f;

    /// <inheritdoc />
    public override string Name => "selu";

    /// <inheritdoc />
    protected override float Apply(float value) =>
        Lambda * (value > 0f ? value : Alpha * (MathF.Exp(value) - 1f));
}

/// <summary>Element-wise GELU activation: gelu(x) ≈ x * σ(1.702x).</summary>
public sealed class GeluFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "gelu";

    /// <inheritdoc />
    protected override float Apply(float value) =>
        value * (1f / (1f + MathF.Exp(-1.702f * value)));
}

/// <summary>Element-wise Swish activation: swish(x) = x * σ(x).</summary>
public sealed class SwishFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "swish";

    /// <inheritdoc />
    protected override float Apply(float value) =>
        value * (1f / (1f + MathF.Exp(-value)));
}

/// <summary>Element-wise Softplus activation: softplus(x) = ln(1 + eˣ).</summary>
public sealed class SoftplusFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "softplus";

    /// <inheritdoc />
    protected override float Apply(float value) => MathF.Log(1f + MathF.Exp(value));
}

/// <summary>Element-wise Softsign activation: softsign(x) = x / (1 + |x|).</summary>
public sealed class SoftsignFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "softsign";

    /// <inheritdoc />
    protected override float Apply(float value) => value / (1f + MathF.Abs(value));
}

/// <summary>Element-wise Mish activation: mish(x) = x * tanh(softplus(x)).</summary>
public sealed class MishFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "mish";

    /// <inheritdoc />
    protected override float Apply(float value) =>
        value * MathF.Tanh(MathF.Log(1f + MathF.Exp(value)));
}

/// <summary>Element-wise Hard Sigmoid: hard_sigmoid(x) = clip((x + 3) / 6, 0, 1).</summary>
public sealed class HardSigmoidFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "hard_sigmoid";

    /// <inheritdoc />
    protected override float Apply(float value)
    {
        float result = (value + 3f) / 6f;
        return MathF.Max(0f, MathF.Min(1f, result));
    }
}

/// <summary>Element-wise Hard Swish: hard_swish(x) = x * hard_sigmoid(x).</summary>
public sealed class HardSwishFunction : UnaryMathFunction
{
    /// <inheritdoc />
    public override string Name => "hard_swish";

    /// <inheritdoc />
    protected override float Apply(float value)
    {
        float hardSigmoid = MathF.Max(0f, MathF.Min(1f, (value + 3f) / 6f));
        return value * hardSigmoid;
    }
}

/// <summary>
/// Leaky ReLU activation: leaky_relu(x) or leaky_relu(x, alpha).
/// Default alpha = 0.01. Returns x if x > 0, else alpha * x.
/// </summary>
public sealed class LeakyReluFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "leaky_relu";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("leaky_relu() requires 1 or 2 arguments.");
        }

        DataKind kind = argumentKinds[0];
        if (kind is not (DataKind.Scalar or DataKind.UInt8 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"leaky_relu() does not support {kind}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException("leaky_relu() second argument (alpha) must be Scalar or UInt8.");
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

        float alpha = 0.01f;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            alpha = arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsScalar();
        }

        float LeakyRelu(float v) => v > 0f ? v : alpha * v;

        switch (input.Kind)
        {
            case DataKind.UInt8:
                return DataValue.FromScalar(LeakyRelu(input.AsUInt8()));
            case DataKind.Scalar:
                return DataValue.FromScalar(LeakyRelu(input.AsScalar()));
            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = LeakyRelu(source[i]);
                return DataValue.FromVector(result);
            }
            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = LeakyRelu(source[i]);
                return DataValue.FromMatrix(result, rows, columns);
            }
            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = LeakyRelu(source[i]);
                return DataValue.FromTensor(result, shape);
            }
            default:
                throw new InvalidOperationException($"leaky_relu() does not support {input.Kind}.");
        }
    }
}

/// <summary>
/// ELU activation: elu(x) or elu(x, alpha).
/// Default alpha = 1.0. Returns x if x > 0, else alpha * (eˣ - 1).
/// </summary>
public sealed class EluFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "elu";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("elu() requires 1 or 2 arguments.");
        }

        DataKind kind = argumentKinds[0];
        if (kind is not (DataKind.Scalar or DataKind.UInt8 or DataKind.Vector or DataKind.Matrix or DataKind.Tensor))
        {
            throw new ArgumentException($"elu() does not support {kind}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException("elu() second argument (alpha) must be Scalar or UInt8.");
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

        float alpha = 1.0f;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            alpha = arguments[1].Kind is DataKind.UInt8 ? arguments[1].AsUInt8() : arguments[1].AsScalar();
        }

        float Elu(float v) => v > 0f ? v : alpha * (MathF.Exp(v) - 1f);

        switch (input.Kind)
        {
            case DataKind.UInt8:
                return DataValue.FromScalar(Elu(input.AsUInt8()));
            case DataKind.Scalar:
                return DataValue.FromScalar(Elu(input.AsScalar()));
            case DataKind.Vector:
            {
                float[] source = input.AsVector();
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Elu(source[i]);
                return DataValue.FromVector(result);
            }
            case DataKind.Matrix:
            {
                float[] source = input.AsMatrix(out int rows, out int columns);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Elu(source[i]);
                return DataValue.FromMatrix(result, rows, columns);
            }
            case DataKind.Tensor:
            {
                float[] source = input.AsTensor(out int[] shape);
                float[] result = new float[source.Length];
                for (int i = 0; i < source.Length; i++) result[i] = Elu(source[i]);
                return DataValue.FromTensor(result, shape);
            }
            default:
                throw new InvalidOperationException($"elu() does not support {input.Kind}.");
        }
    }
}
