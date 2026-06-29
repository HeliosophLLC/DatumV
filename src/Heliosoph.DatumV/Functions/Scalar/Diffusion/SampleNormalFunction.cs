using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Diffusion;

/// <summary>
/// <c>sample_normal(count Int32 [, seed Int64]) → Float32[]</c>. Draws
/// <c>count</c> independent samples from the standard normal distribution
/// N(0, 1). Without a <c>seed</c> — or with a NULL <c>seed</c> — the source is
/// the process-wide <see cref="Random.Shared"/> instance, so every call
/// returns a different draw, mirroring the <c>IsDeterministic = false</c>
/// contract SQL-defined diffusion bodies declare. A non-null <c>seed</c>
/// makes the draw reproducible: the same <c>(count, seed)</c> pair always
/// yields the same array.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Algorithm.</strong> Box-Muller transform: each pair of uniform
/// (0, 1] draws produces two independent N(0, 1) samples via
/// <c>r = sqrt(-2·ln(u1))</c>, <c>θ = 2π·u2</c>,
/// <c>(z1, z2) = (r·cos θ, r·sin θ)</c>. Distribution matches torch.randn
/// closely enough for diffusion noise — SD-class models are robust to
/// small differences in the seed-noise distribution.
/// </para>
/// <para>
/// <strong>Why a built-in.</strong> Expressing Box-Muller in SQL would
/// require either a loop with arithmetic on Float32[] arrays plus per-element
/// trig, or a synthetic Float64 array carrier that doesn't exist today.
/// One scalar builtin is dramatically simpler. The diffusion bodies call
/// this once at the start of each generation to fill the initial latent
/// buffer with sigma-scaled noise.
/// </para>
/// <para>
/// <strong>Reproducibility scope.</strong> Seeding fixes only the initial
/// latent noise. A given seed reproduces this engine's own output on a fixed
/// execution provider; it does not match torch.randn (the distribution is
/// close but not identical) and does not, on its own, defeat non-deterministic
/// GPU kernels downstream.
/// </para>
/// </remarks>
public sealed class SampleNormalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sample_normal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Numeric;

    /// <inheritdoc />
    public static string Description =>
        "Draws `count` independent samples from N(0, 1) via Box-Muller and returns Float32[]. " +
        "With no seed (or a NULL seed) it uses Random.Shared, so every call produces a different " +
        "draw; an explicit integer seed makes the draw reproducible. " +
        "Used by diffusion model bodies to fill the initial latent buffer with Gaussian noise.";

    private static readonly ReturnTypeRule Float32ArrayReturn =
        ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32));

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("seed", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: Float32ArrayReturn),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SampleNormalFunction>(argumentKinds);

    /// <summary>
    /// Not pure: the unseeded form draws from <see cref="Random.Shared"/>, so
    /// CSE must never collapse two textual call sites into one evaluation.
    /// </summary>
    public bool IsPure => false;

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        if (!args[0].TryToInt32(out int count))
        {
            throw new FunctionArgumentException(Name,
                $"count of kind {args[0].Kind} could not be widened to Int32.");
        }
        if (count < 0)
        {
            throw new FunctionArgumentException(Name,
                $"count must be non-negative; got {count}.");
        }
        if (count == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32));
        }

        // A non-null seed makes the draw reproducible; otherwise (no seed arg,
        // or an explicit NULL seed) fall back to the shared RNG.
        Random rng;
        if (args.Length > 1 && !args[1].IsNull)
        {
            if (!args[1].TryToInt64(out long seed))
            {
                throw new FunctionArgumentException(Name,
                    $"seed of kind {args[1].Kind} could not be widened to Int64.");
            }
            rng = new Random(unchecked((int)seed));
        }
        else
        {
            rng = Random.Shared;
        }

        float[] noise = new float[count];
        for (int i = 0; i < count; i += 2)
        {
            // Box-Muller: 1 - rng to map (0, 1] (avoids log(0) when the draw is exactly 0).
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double mag = System.Math.Sqrt(-2.0 * System.Math.Log(u1));
            double angle = 2.0 * System.Math.PI * u2;
            noise[i] = (float)(mag * System.Math.Cos(angle));
            if (i + 1 < count)
            {
                noise[i + 1] = (float)(mag * System.Math.Sin(angle));
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(noise, DataKind.Float32));
    }
}
