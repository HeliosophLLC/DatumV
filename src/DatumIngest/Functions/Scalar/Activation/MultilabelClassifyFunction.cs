using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Activation;

/// <summary>
/// <c>multilabel_classify(logits FLOAT32[], labels STRING[], threshold FLOAT32) →
/// Array&lt;ScoredLabel&gt;</c>. Multi-label classifier postprocess: applies
/// sigmoid per-logit, filters to labels whose probability ≥ <c>threshold</c>,
/// and emits one <c>ScoredLabel</c> per surviving label in input order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a bundled function.</strong> Multi-label classifiers
/// (toxicity, content tags, multi-attribute detection) emit one logit per
/// label and treat each independently — no softmax, no argmax. The
/// canonical postprocess is sigmoid + threshold + zip-with-labels;
/// bundling those into one function keeps the SQL bodies for those models
/// one-liner-clean. Mirrors the way <c>yolox_postprocess</c> bundles
/// detector-specific postprocess.
/// </para>
/// <para>
/// <strong>Threshold convention.</strong> Default 0.5 is conventional for
/// production use. Pass 0.0 to emit every label (debugging / threshold
/// tuning); pass &gt;=1.0 to emit nothing. Threshold is sigmoid-space, not
/// logit-space (i.e. <c>prob ≥ threshold</c>, not <c>logit ≥ threshold</c>).
/// </para>
/// <para>
/// <strong>Output order.</strong> Surviving labels keep their input order.
/// Callers wanting score-descending output can sort downstream
/// (<c>ORDER BY x.score DESC</c> after an <c>UNNEST</c>).
/// </para>
/// </remarks>
public sealed class MultilabelClassifyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "multilabel_classify";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Multi-label classifier postprocess: sigmoid + per-label threshold filter + label-string zip. "
        + "multilabel_classify(logits FLOAT32[], labels STRING[], threshold FLOAT32) → "
        + "Array<ScoredLabel>. Threshold is sigmoid-space (0.0 emits all, 0.5 typical, ≥1 emits none).";

    /// <summary>Output named type — surfaces fields as label + score.</summary>
    public const string ResultNamedType = "ScoredLabel";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("logits",    DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("labels",    DataKindMatcher.Exact(DataKind.String),  IsArray: ArrayMatch.Array),
                new ParameterSpec("threshold", DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Struct))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MultilabelClassifyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, []));
        }

        float[] logits = ActivationOps.ReadFloat32Array(args[0]);
        string[] labels = ReadStringArray(args[1]);
        float threshold = args[2].AsFloat32();

        if (logits.Length != labels.Length)
        {
            throw new FunctionArgumentException(Name,
                $"logits length ({logits.Length}) must match labels length ({labels.Length}); "
                + "each logit is the score for the label at the same index.");
        }

        // First pass: count survivors so we allocate exactly once.
        int surviving = 0;
        float[] probs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            // Numerically-stable sigmoid: avoid exp(+big) overflow by
            // handling positive and negative branches separately.
            float x = logits[i];
            float p = x >= 0
                ? 1f / (1f + MathF.Exp(-x))
                : MathF.Exp(x) / (1f + MathF.Exp(x));
            probs[i] = p;
            if (p >= threshold) surviving++;
        }

        if (surviving == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, []));
        }

        ushort scoredLabelTypeId = frame.Types is { } types
            ? (ushort)types.GetTypeIdByName(ResultNamedType)
            : (ushort)0;

        ValueRef[] elements = new ValueRef[surviving];
        int cursor = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            if (probs[i] < threshold) continue;
            ValueRef[] structFields =
            [
                ValueRef.FromString(labels[i]),
                ValueRef.FromFloat32(probs[i]),
            ];
            elements[cursor++] = scoredLabelTypeId == 0
                ? ValueRef.FromStruct(structFields)
                : ValueRef.FromStruct(structFields, scoredLabelTypeId);
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Struct, elements));
    }

    private static string[] ReadStringArray(ValueRef arg)
    {
        if (arg.Materialized is string[] direct) return direct;
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        string[] result = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            result[i] = elements[i].IsNull ? string.Empty : elements[i].AsString();
        }
        return result;
    }
}
