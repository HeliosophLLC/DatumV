using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Activation;

/// <summary>
/// Covers <c>multilabel_classify(logits, labels, threshold)</c> — the
/// shared postprocess scalar for multi-label classifiers (Toxic-BERT,
/// content-tag classifiers, attribute classifiers).
/// </summary>
public sealed class MultilabelClassifyFunctionTests : ServiceTestBase
{
    private async Task<ValueRef> InvokeAsync(float[] logits, string[] labels, float threshold)
    {
        MultilabelClassifyFunction fn = new();
        ValueRef[] labelRefs = new ValueRef[labels.Length];
        for (int i = 0; i < labels.Length; i++) labelRefs[i] = ValueRef.FromString(labels[i]);

        return await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                ValueRef.FromArray(DataKind.String, labelRefs),
                ValueRef.FromFloat32(threshold),
            }.AsMemory(),
            CreateEvaluationFrame(), CancellationToken.None);
    }

    [Fact]
    public async Task LogitsAboveThreshold_EmittedInInputOrder()
    {
        // Logits: 2 (sigmoid≈0.88), -3 (≈0.05), 1 (≈0.73), 4 (≈0.98).
        // Threshold 0.5 → keep indices 0, 2, 3 → ['toxic', 'obscene', 'insult'].
        ValueRef result = await InvokeAsync(
            logits: [2f, -3f, 1f, 4f],
            labels: ["toxic", "severe_toxic", "obscene", "insult"],
            threshold: 0.5f);

        Assert.True(result.IsArray);
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(3, elements.Length);

        // First survivor: 'toxic' with p ≈ 0.88.
        ReadOnlySpan<ValueRef> f0 = elements[0].GetStructFields();
        Assert.Equal("toxic", f0[0].AsString());
        Assert.InRange(f0[1].AsFloat32(), 0.87f, 0.89f);

        // Third survivor: 'insult' with p ≈ 0.98 — must keep input order
        // (not score-descending).
        ReadOnlySpan<ValueRef> f2 = elements[2].GetStructFields();
        Assert.Equal("insult", f2[0].AsString());
        Assert.InRange(f2[1].AsFloat32(), 0.97f, 0.99f);
    }

    [Fact]
    public async Task NoLogitsAboveThreshold_ReturnsEmptyArray()
    {
        // All negative logits → sigmoid < 0.5 everywhere → nothing emitted.
        ValueRef result = await InvokeAsync(
            logits: [-1f, -2f, -5f],
            labels: ["a", "b", "c"],
            threshold: 0.5f);

        Assert.True(result.IsArray);
        Assert.Equal(0, result.GetArrayElements().Length);
    }

    [Fact]
    public async Task ThresholdZero_EmitsEveryLabel()
    {
        // Threshold 0 → all sigmoid > 0 trivially → emit every label.
        ValueRef result = await InvokeAsync(
            logits: [-5f, 0f, 5f],
            labels: ["a", "b", "c"],
            threshold: 0f);

        Assert.True(result.IsArray);
        Assert.Equal(3, result.GetArrayElements().Length);
    }

    [Fact]
    public async Task MismatchedLengths_ThrowsFunctionArgumentException()
    {
        MultilabelClassifyFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromPrimitiveArray(new[] { 1f, 2f, 3f }, DataKind.Float32),
                    ValueRef.FromArray(DataKind.String, new[]
                    {
                        ValueRef.FromString("a"),
                        ValueRef.FromString("b"),
                    }),
                    ValueRef.FromFloat32(0.5f),
                }.AsMemory(),
                CreateEvaluationFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task NumericallyStableForLargePositiveAndNegativeLogits()
    {
        // Very negative + very positive logits would overflow a naive
        // sigmoid (exp(+50) → +inf, exp(-50) ≈ 0). The numerically-stable
        // formulation handles both extremes.
        ValueRef result = await InvokeAsync(
            logits: [-50f, 50f],
            labels: ["a", "b"],
            threshold: 0f);

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(2, elements.Length);

        float pA = elements[0].GetStructFields()[1].AsFloat32();
        float pB = elements[1].GetStructFields()[1].AsFloat32();

        // Both should be in [0, 1] and not NaN / inf.
        Assert.InRange(pA, 0f, 1f);
        Assert.InRange(pB, 0f, 1f);
        Assert.True(pA < 1e-10f, "very negative logit → ~0 probability");
        Assert.True(pB > 0.999999f, "very positive logit → ~1 probability");
    }
}
