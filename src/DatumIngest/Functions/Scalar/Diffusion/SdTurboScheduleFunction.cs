using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Diffusion;

/// <summary>
/// <c>sd_turbo_schedule(steps Int32) → Struct{sigmas: Float32[], timesteps: Float32[]}</c>.
/// Computes the noise schedule for SD-Turbo / SDXL-Turbo and their distilled
/// variants (Hyper-SD, Lightning, etc.). Returns the per-step training
/// timesteps the UNet was conditioned on, plus the matching sigmas
/// (length <c>steps + 1</c>; last entry is zero) used by the Euler update.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why "trailing" spacing, not Karras.</strong> SD-Turbo, SDXL-Turbo,
/// and the Hyper-SD / Lightning distillations were trained at specific
/// noise levels — uniform-stride training timesteps from the training-time
/// 1000-step schedule. Using Karras ρ-spacing here sends intermediate
/// queries outside the distillation distribution and produces degenerate
/// output (overlapping faces, blur). This builtin encodes the exact spacing
/// the models expect.
/// </para>
/// <para>
/// <strong>Beta schedule.</strong> Scaled-linear betas from 0.00085 to 0.012
/// across 1000 timesteps (the SD 1.x / SD 2.x / SDXL canonical schedule).
/// The same builtin serves all variants because they share the same
/// training noise schedule — only <c>steps</c> varies.
/// </para>
/// <para>
/// <strong>Output.</strong> A struct with two fields:
/// <list type="bullet">
///   <item><c>sigmas</c> — Float32[<c>steps + 1</c>]. Index 0 is sigma_max
///   (initial noise scale); index <c>steps</c> is 0 (clean image).</item>
///   <item><c>timesteps</c> — Float32[<c>steps</c>]. Per-step training
///   timesteps in [0, 999] for the UNet's <c>timestep</c> input.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SdTurboScheduleFunction : IFunction, IScalarFunction
{
    private const float BetaStart = 0.00085f;
    private const float BetaEnd = 0.012f;
    private const int NumTrainSteps = 1000;

    /// <inheritdoc />
    public static string Name => "sd_turbo_schedule";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Computes the SD-Turbo / SDXL-Turbo / Hyper-SD denoising schedule for a given step count. " +
        "Returns Struct{sigmas Float32[steps+1], timesteps Float32[steps]} where sigmas[0] is the " +
        "initial noise scale, sigmas[steps] = 0, and timesteps are the per-step UNet conditioning " +
        "values in [0, 999]. Trailing spacing (NOT Karras) — distilled SD models were trained at " +
        "specific noise levels and Karras spacing degrades output quality.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("steps", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SdTurboScheduleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }
        if (!args[0].TryToInt32(out int steps))
        {
            throw new FunctionArgumentException(Name,
                $"steps of kind {args[0].Kind} could not be widened to Int32.");
        }
        if (steps <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"steps must be positive; got {steps}.");
        }

        // Precompute sigma for every training timestep via the SD scaled-linear
        // beta schedule. cumAlpha is the running product of (1 - beta_t).
        float[] trainSigmas = new float[NumTrainSteps];
        float cumAlpha = 1f;
        for (int i = 0; i < NumTrainSteps; i++)
        {
            float t = (float)i / (NumTrainSteps - 1);
            float sqrtBeta = MathF.Sqrt(BetaStart) + t * (MathF.Sqrt(BetaEnd) - MathF.Sqrt(BetaStart));
            cumAlpha *= 1f - sqrtBeta * sqrtBeta;
            trainSigmas[i] = MathF.Sqrt((1f - cumAlpha) / cumAlpha);
        }

        // Trailing-spaced training timesteps: round(NumTrainSteps - i*ratio) - 1.
        // steps=4 → [999, 749, 499, 249]; steps=1 → [999].
        float stepRatio = (float)NumTrainSteps / steps;
        float[] sigmas = new float[steps + 1];
        float[] timesteps = new float[steps];
        for (int i = 0; i < steps; i++)
        {
            int t = (int)MathF.Round(NumTrainSteps - i * stepRatio) - 1;
            t = System.Math.Clamp(t, 0, NumTrainSteps - 1);
            timesteps[i] = t;
            sigmas[i] = trainSigmas[t];
        }
        sigmas[steps] = 0f;

        ValueRef[] fields =
        [
            ValueRef.FromPrimitiveArray(sigmas,    DataKind.Float32),
            ValueRef.FromPrimitiveArray(timesteps, DataKind.Float32),
        ];

        ushort typeId = 0;
        if (frame.Types is { } types)
        {
            int float32ArrayTypeId = types.InternArrayType(DataKind.Float32);
            StructFieldDescriptor[] descriptors =
            [
                new("sigmas",    float32ArrayTypeId),
                new("timesteps", float32ArrayTypeId),
            ];
            typeId = (ushort)types.InternStructType(descriptors);
        }

        return new ValueTask<ValueRef>(ValueRef.FromStruct(fields, typeId));
    }
}
