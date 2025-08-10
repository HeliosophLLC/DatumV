using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Samples from a truncated normal distribution:
/// <c>random_truncated_normal(mean, stddev, min, max)</c>.
/// Rejection-samples from N(mean, stddev) until the value falls within [min, max].
/// Common in ML weight initialization (cf. TensorFlow's <c>tf.random.truncated_normal</c>).
/// </summary>
public sealed class RandomTruncatedNormalFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_truncated_normal";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 4)
            throw new ArgumentException("random_truncated_normal() requires exactly 4 arguments (mean, stddev, min, max).");

        for (int i = 0; i < 4; i++)
        {
            if (argumentKinds[i] is not (DataKind.Scalar or DataKind.UInt8))
                throw new ArgumentException($"random_truncated_normal() argument {i + 1} must be Scalar or UInt8, got {argumentKinds[i]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float mean = ExtractFloat(arguments[0]);
        float stddev = ExtractFloat(arguments[1]);
        float min = ExtractFloat(arguments[2]);
        float max = ExtractFloat(arguments[3]);

        if (stddev < 0)
            throw new ArgumentException($"random_truncated_normal() stddev must be non-negative, got {stddev}.");
        if (min >= max)
            throw new ArgumentException($"random_truncated_normal() min ({min}) must be < max ({max}).");

        // Rejection sampling with a safety limit to avoid infinite loops on pathological parameters.
        const int maximumAttempts = 1000;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            float sample = mean + stddev * RandomNormalFunction.SampleStandardNormal();
            if (sample >= min && sample <= max)
                return DataValue.FromScalar(sample);
        }

        // Fallback: clamp to range if rejection sampling exhausted (degenerate parameters).
        float fallback = mean + stddev * RandomNormalFunction.SampleStandardNormal();
        return DataValue.FromScalar(System.Math.Clamp(fallback, min, max));
    }

    private static float ExtractFloat(DataValue value) =>
        value.Kind is DataKind.UInt8 ? value.AsUInt8() : value.AsScalar();
}

/// <summary>
/// Samples from a log-normal distribution: <c>random_log_normal(mean, stddev)</c>.
/// Returns exp(N(mean, stddev)). Common for modeling durations, prices, and sizes.
/// </summary>
public sealed class RandomLogNormalFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_log_normal";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_log_normal() requires exactly 2 arguments (mean, stddev).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_log_normal() mean (argument 1) must be Scalar or UInt8, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_log_normal() stddev (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float mean = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar();

        float stddev = arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsScalar();

        if (stddev < 0)
            throw new ArgumentException($"random_log_normal() stddev must be non-negative, got {stddev}.");

        float normalSample = mean + stddev * RandomNormalFunction.SampleStandardNormal();
        return DataValue.FromScalar(MathF.Exp(normalSample));
    }
}

/// <summary>
/// Samples from an exponential distribution: <c>random_exponential(rate)</c>.
/// Returns -ln(U) / rate where U is uniform in (0, 1].
/// Common for modeling inter-arrival times and decay processes.
/// </summary>
public sealed class RandomExponentialFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_exponential";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_exponential() requires exactly 1 argument (rate).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_exponential() rate must be Scalar or UInt8, got {argumentKinds[0]}.");

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float rate = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar();

        if (rate <= 0)
            throw new ArgumentException($"random_exponential() rate must be positive, got {rate}.");

        // 1 - NextDouble() avoids log(0).
        double sample = -System.Math.Log(1.0 - Random.Shared.NextDouble()) / rate;
        return DataValue.FromScalar((float)sample);
    }
}

/// <summary>
/// Samples from a Beta distribution: <c>random_beta(alpha, beta)</c>.
/// Uses Jöhnk's algorithm for small parameters and the gamma-ratio method otherwise.
/// Common for prior distributions over probabilities and mixup augmentation.
/// </summary>
public sealed class RandomBetaFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_beta";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("random_beta() requires exactly 2 arguments (alpha, beta).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_beta() alpha (argument 1) must be Scalar or UInt8, got {argumentKinds[0]}.");

        if (argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_beta() beta (argument 2) must be Scalar or UInt8, got {argumentKinds[1]}.");

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float alpha = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar();

        float beta = arguments[1].Kind is DataKind.UInt8
            ? arguments[1].AsUInt8()
            : arguments[1].AsScalar();

        if (alpha <= 0)
            throw new ArgumentException($"random_beta() alpha must be positive, got {alpha}.");
        if (beta <= 0)
            throw new ArgumentException($"random_beta() beta must be positive, got {beta}.");

        double x = SampleGamma(alpha);
        double y = SampleGamma(beta);
        return DataValue.FromScalar((float)(x / (x + y)));
    }

    /// <summary>
    /// Samples from Gamma(shape, 1) using Marsaglia and Tsang's method.
    /// </summary>
    internal static double SampleGamma(double shape)
    {
        if (shape < 1.0)
        {
            // Boost: Gamma(shape) = Gamma(shape + 1) * U^(1/shape)
            double boost = System.Math.Pow(Random.Shared.NextDouble(), 1.0 / shape);
            return SampleGamma(shape + 1.0) * boost;
        }

        double d = shape - 1.0 / 3.0;
        double c = 1.0 / System.Math.Sqrt(9.0 * d);

        while (true)
        {
            double x = RandomNormalFunction.SampleStandardNormal();
            double v = 1.0 + c * x;
            if (v <= 0) continue;
            v = v * v * v;
            double u = Random.Shared.NextDouble();
            if (u < 1.0 - 0.0331 * (x * x) * (x * x))
                return d * v;
            if (System.Math.Log(u) < 0.5 * x * x + d * (1.0 - v + System.Math.Log(v)))
                return d * v;
        }
    }
}

/// <summary>
/// Samples from a Poisson distribution: <c>random_poisson(lambda)</c>.
/// Returns a non-negative integer count drawn from Poisson(λ).
/// Uses Knuth's algorithm for λ ≤ 30 and the normal approximation for larger λ.
/// Common for count data augmentation.
/// </summary>
public sealed class RandomPoissonFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_poisson";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_poisson() requires exactly 1 argument (lambda).");

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException($"random_poisson() lambda must be Scalar or UInt8, got {argumentKinds[0]}.");

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float lambda = arguments[0].Kind is DataKind.UInt8
            ? arguments[0].AsUInt8()
            : arguments[0].AsScalar();

        if (lambda < 0)
            throw new ArgumentException($"random_poisson() lambda must be non-negative, got {lambda}.");

        if (lambda == 0)
            return DataValue.FromScalar(0f);

        int count;
        if (lambda <= 30)
        {
            // Knuth's algorithm for small lambda.
            double limit = System.Math.Exp(-lambda);
            count = -1;
            double product = 1.0;
            do
            {
                count++;
                product *= Random.Shared.NextDouble();
            } while (product > limit);
        }
        else
        {
            // Normal approximation for large lambda.
            double sample = lambda + System.Math.Sqrt(lambda) * RandomNormalFunction.SampleStandardNormal();
            count = System.Math.Max(0, (int)System.Math.Round(sample));
        }

        return DataValue.FromScalar(count);
    }
}

/// <summary>
/// Draws a 0-based category index from weighted probabilities:
/// <c>random_categorical(weights)</c> where <c>weights</c> is a Vector of non-negative
/// values (need not sum to 1 — they are normalized internally).
/// Useful for synthetic label generation and weighted random selection.
/// </summary>
public sealed class RandomCategoricalFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random_categorical";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("random_categorical() requires exactly 1 argument (weights vector).");

        if (argumentKinds[0] != DataKind.Vector)
            throw new ArgumentException($"random_categorical() requires a Vector argument, got {argumentKinds[0]}.");

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
            return DataValue.Null(DataKind.Scalar);

        float[] weights = arguments[0].AsVector();
        if (weights.Length == 0)
            throw new ArgumentException("random_categorical() weights vector must not be empty.");

        double total = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < 0)
                throw new ArgumentException($"random_categorical() weights must be non-negative, got {weights[i]} at index {i}.");
            total += weights[i];
        }

        if (total <= 0)
            throw new ArgumentException("random_categorical() weights must sum to a positive value.");

        double threshold = Random.Shared.NextDouble() * total;
        double cumulative = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (threshold < cumulative)
                return DataValue.FromScalar(i);
        }

        // Floating-point edge case — return the last category.
        return DataValue.FromScalar(weights.Length - 1);
    }
}
