using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Functions.Scalar.Image;
using Heliosoph.DatumV.Functions.Scalar.Vector;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Direct-invocation tests for the Tier 3 postprocess helpers: softmax,
/// sigmoid, argmax, topk, l2_normalize, cosine_similarity, nms,
/// tensor_to_image_chw, mask_to_polygon.
/// </summary>
/// <remarks>
/// Invokes each function class directly with a synthetic
/// <see cref="EvaluationFrame"/> — sidesteps the catalog arena-lifecycle
/// plumbing required to feed array values through a SQL plan and keeps
/// the assertions focused on the math.
/// </remarks>
public sealed class Tier3PostprocessTests : ServiceTestBase
{
    private static ValueRef F32(params float[] values) =>
        ValueRef.FromPrimitiveArray(values, DataKind.Float32);

    private async Task<ValueRef> InvokeAsync(IScalarFunction fn, params ValueRef[] args)
        => await fn.ExecuteAsync(args.AsMemory(), CreateEvaluationFrame(), CancellationToken.None);

    private static float[] AsFloatArr(ValueRef v) => (float[])v.Materialized!;
    private static int[] AsIntArr(ValueRef v) => (int[])v.Materialized!;

    // ─── softmax ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Softmax_KnownInput_MatchesCanonicalValues()
    {
        ValueRef result = await InvokeAsync(new SoftmaxFunction(), F32(1f, 2f, 3f));
        float[] probs = AsFloatArr(result);

        // softmax([1, 2, 3]) ≈ [0.0900, 0.2447, 0.6652]
        Assert.Equal(3, probs.Length);
        Assert.Equal(0.09003f, probs[0], 4);
        Assert.Equal(0.24473f, probs[1], 4);
        Assert.Equal(0.66524f, probs[2], 4);

        float sum = probs[0] + probs[1] + probs[2];
        Assert.Equal(1f, sum, 5);
    }

    [Fact]
    public async Task Softmax_LargeMagnitudes_DoesntOverflow()
    {
        // exp(1000) overflows to +inf without the max-subtract trick.
        ValueRef result = await InvokeAsync(new SoftmaxFunction(), F32(1000f, 1001f, 1002f));
        float[] probs = AsFloatArr(result);
        Assert.All(probs, p => Assert.True(!float.IsNaN(p) && !float.IsInfinity(p)));
        float sum = probs[0] + probs[1] + probs[2];
        Assert.Equal(1f, sum, 5);
    }

    // ─── sigmoid ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sigmoid_KnownInputs_MatchExpectedValues()
    {
        ValueRef result = await InvokeAsync(new SigmoidFunction(), F32(0f, 100f, -100f));
        float[] out_ = AsFloatArr(result);
        Assert.Equal(0.5f, out_[0], 5);
        Assert.Equal(1f, out_[1], 5);
        Assert.Equal(0f, out_[2], 5);
    }

    // ─── argmax ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Argmax_TieBreaksToLowestIndex()
    {
        // 1-based output: ties resolve to the lowest 1-based index.
        ValueRef result = await InvokeAsync(new ArgmaxFunction(), F32(0.5f, 0.5f, 0.5f));
        Assert.Equal(1, result.ToInt32());
    }

    [Fact]
    public async Task Argmax_PicksTheLargest()
    {
        // 1-based output: element 0.7 is at 1-based index 2.
        ValueRef result = await InvokeAsync(new ArgmaxFunction(), F32(0.1f, 0.7f, 0.2f));
        Assert.Equal(2, result.ToInt32());
    }

    [Fact]
    public async Task Argmax_EmptyInput_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(new ArgmaxFunction(), F32()));
    }

    // ─── topk ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Topk_ReturnsIndicesDescending()
    {
        // 1-based output. values: [0.1, 0.5, 0.3, 0.9, 0.2] — top-3 indices by
        // value descending are [4 (0.9), 2 (0.5), 3 (0.3)].
        ValueRef result = await InvokeAsync(new TopkFunction(),
            F32(0.1f, 0.5f, 0.3f, 0.9f, 0.2f),
            ValueRef.FromInt32(3));
        Assert.Equal([4, 2, 3], AsIntArr(result));
    }

    [Fact]
    public async Task Topk_KExceedingLength_ReturnsAllIndices()
    {
        ValueRef result = await InvokeAsync(new TopkFunction(),
            F32(1f, 2f, 3f),
            ValueRef.FromInt32(10));
        int[] indices = AsIntArr(result);
        Assert.Equal(3, indices.Length);
    }

    // ─── l2_normalize ────────────────────────────────────────────────────────

    [Fact]
    public async Task L2Normalize_3_4_Normalizes_To_0p6_0p8()
    {
        ValueRef result = await InvokeAsync(new L2NormalizeFunction(), F32(3f, 4f));
        float[] u = AsFloatArr(result);
        Assert.Equal(0.6f, u[0], 5);
        Assert.Equal(0.8f, u[1], 5);
    }

    [Fact]
    public async Task L2Normalize_AllZero_ReturnsAllZero()
    {
        ValueRef result = await InvokeAsync(new L2NormalizeFunction(), F32(0f, 0f, 0f));
        Assert.All(AsFloatArr(result), v => Assert.Equal(0f, v));
    }

    // ─── cosine_similarity ───────────────────────────────────────────────────

    [Fact]
    public async Task CosineSimilarity_Identical_Returns1()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 2f, 3f), F32(1f, 2f, 3f));
        Assert.Equal(1f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_Orthogonal_Returns0()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 0f, 0f), F32(0f, 1f, 0f));
        Assert.Equal(0f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_Opposite_ReturnsMinus1()
    {
        ValueRef result = await InvokeAsync(new CosineSimilarityFunction(),
            F32(1f, 0f), F32(-1f, 0f));
        Assert.Equal(-1f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task CosineSimilarity_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new CosineSimilarityFunction(), F32(1f, 2f), F32(1f, 2f, 3f)));
    }

    // ─── dot_product ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DotProduct_KnownInputs_MatchesExpectedSum()
    {
        // [1,2,3] · [4,5,6] = 4 + 10 + 18 = 32
        ValueRef result = await InvokeAsync(new DotProductFunction(),
            F32(1f, 2f, 3f), F32(4f, 5f, 6f));
        Assert.Equal(32f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task DotProduct_Orthogonal_Returns0()
    {
        ValueRef result = await InvokeAsync(new DotProductFunction(),
            F32(1f, 0f, 0f), F32(0f, 1f, 0f));
        Assert.Equal(0f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task DotProduct_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new DotProductFunction(), F32(1f, 2f), F32(1f, 2f, 3f)));
    }

    // ─── euclidean_distance ──────────────────────────────────────────────────

    [Fact]
    public async Task EuclideanDistance_KnownInputs_MatchesPythagorean()
    {
        // sqrt((3-0)² + (4-0)²) = 5
        ValueRef result = await InvokeAsync(new EuclideanDistanceFunction(),
            F32(3f, 4f), F32(0f, 0f));
        Assert.Equal(5f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task EuclideanDistance_Identical_Returns0()
    {
        ValueRef result = await InvokeAsync(new EuclideanDistanceFunction(),
            F32(1f, 2f, 3f), F32(1f, 2f, 3f));
        Assert.Equal(0f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task EuclideanDistance_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new EuclideanDistanceFunction(), F32(1f, 2f), F32(1f, 2f, 3f)));
    }

    // ─── manhattan_distance ──────────────────────────────────────────────────

    [Fact]
    public async Task ManhattanDistance_KnownInputs_SumsAbsoluteDifferences()
    {
        // |1-4| + |2-6| + |3-8| = 3 + 4 + 5 = 12
        ValueRef result = await InvokeAsync(new ManhattanDistanceFunction(),
            F32(1f, 2f, 3f), F32(4f, 6f, 8f));
        Assert.Equal(12f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task ManhattanDistance_NegativeDifferences_TakesAbsoluteValue()
    {
        // |-1-1| + |-2-2| = 2 + 4 = 6
        ValueRef result = await InvokeAsync(new ManhattanDistanceFunction(),
            F32(-1f, -2f), F32(1f, 2f));
        Assert.Equal(6f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task ManhattanDistance_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new ManhattanDistanceFunction(), F32(1f, 2f), F32(1f, 2f, 3f)));
    }

    // ─── hamming_distance ────────────────────────────────────────────────────

    [Fact]
    public async Task HammingDistance_CountsDifferingPositions()
    {
        // "karolin" vs "kathrin": positions 2,3,4 differ → 3
        ValueRef result = await InvokeAsync(new HammingDistanceFunction(),
            ValueRef.FromString("karolin"), ValueRef.FromString("kathrin"));
        Assert.Equal(3f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task HammingDistance_Identical_Returns0()
    {
        ValueRef result = await InvokeAsync(new HammingDistanceFunction(),
            ValueRef.FromString("hello"), ValueRef.FromString("hello"));
        Assert.Equal(0f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task HammingDistance_LengthMismatch_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new HammingDistanceFunction(),
                ValueRef.FromString("abc"), ValueRef.FromString("abcd")));
    }

    // ─── vec_sum / vec_mean / vec_product ────────────────────────────────────

    [Fact]
    public async Task VecSum_AccumulatesAndEmptyReturnsZero()
    {
        Assert.Equal(6f, (float)(await InvokeAsync(new VecSumFunction(), F32(1f, 2f, 3f))).ToDouble(), 5);
        Assert.Equal(0f, (float)(await InvokeAsync(new VecSumFunction(), F32())).ToDouble(), 5);
    }

    [Fact]
    public async Task VecMean_AveragesAndEmptyReturnsNull()
    {
        Assert.Equal(2f, (float)(await InvokeAsync(new VecMeanFunction(), F32(1f, 2f, 3f))).ToDouble(), 5);
        Assert.True((await InvokeAsync(new VecMeanFunction(), F32())).IsNull);
    }

    [Fact]
    public async Task VecProduct_MultipliesAndEmptyReturnsOne()
    {
        Assert.Equal(24f, (float)(await InvokeAsync(new VecProductFunction(), F32(1f, 2f, 3f, 4f))).ToDouble(), 5);
        Assert.Equal(1f, (float)(await InvokeAsync(new VecProductFunction(), F32())).ToDouble(), 5);
    }

    // ─── vec_min / vec_max ───────────────────────────────────────────────────

    [Fact]
    public async Task VecMinMax_KnownInputs_AndEmptyReturnsNull()
    {
        Assert.Equal(-3f, (float)(await InvokeAsync(new VecMinFunction(), F32(2f, -3f, 5f, 1f))).ToDouble(), 5);
        Assert.Equal(5f, (float)(await InvokeAsync(new VecMaxFunction(), F32(2f, -3f, 5f, 1f))).ToDouble(), 5);
        Assert.True((await InvokeAsync(new VecMinFunction(), F32())).IsNull);
        Assert.True((await InvokeAsync(new VecMaxFunction(), F32())).IsNull);
    }

    // ─── vec_var / vec_std ───────────────────────────────────────────────────

    [Fact]
    public async Task VecVarStd_PopulationFormula()
    {
        // [2,4,4,4,5,5,7,9] population variance = 4, std = 2 (classic worked example)
        ValueRef sample = F32(2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f);
        Assert.Equal(4f, (float)(await InvokeAsync(new VecVarFunction(), sample)).ToDouble(), 4);
        Assert.Equal(2f, (float)(await InvokeAsync(new VecStdFunction(), sample)).ToDouble(), 4);
    }

    [Fact]
    public async Task VecVarStd_EmptyReturnsNull()
    {
        Assert.True((await InvokeAsync(new VecVarFunction(), F32())).IsNull);
        Assert.True((await InvokeAsync(new VecStdFunction(), F32())).IsNull);
    }

    // ─── vec_median ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VecMedian_OddLength_PicksCentreElement()
    {
        // sorted: [1,2,3,4,5] → 3
        ValueRef result = await InvokeAsync(new VecMedianFunction(), F32(3f, 1f, 4f, 1f, 5f));
        // sorted = [1,1,3,4,5] → 3
        Assert.Equal(3f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task VecMedian_EvenLength_AveragesCentrePair()
    {
        // sorted: [1,2,3,4] → (2+3)/2 = 2.5
        ValueRef result = await InvokeAsync(new VecMedianFunction(), F32(4f, 2f, 1f, 3f));
        Assert.Equal(2.5f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task VecMedian_EmptyReturnsNull()
    {
        Assert.True((await InvokeAsync(new VecMedianFunction(), F32())).IsNull);
    }

    // ─── vec_norm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task VecNorm_DefaultIsL2()
    {
        // ||[3,4]||₂ = 5
        Assert.Equal(5f, (float)(await InvokeAsync(new VecNormFunction(), F32(3f, 4f))).ToDouble(), 5);
    }

    [Fact]
    public async Task VecNorm_L1_SumsAbsoluteValues()
    {
        // ||[-1,2,-3]||₁ = 6
        ValueRef result = await InvokeAsync(new VecNormFunction(), F32(-1f, 2f, -3f), ValueRef.FromFloat32(1f));
        Assert.Equal(6f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task VecNorm_PInfinity_ReturnsMaxAbs()
    {
        // ||[-1,2,-3]||∞ = 3
        ValueRef result = await InvokeAsync(new VecNormFunction(),
            F32(-1f, 2f, -3f), ValueRef.FromFloat32(float.PositiveInfinity));
        Assert.Equal(3f, (float)result.ToDouble(), 5);
    }

    [Fact]
    public async Task VecNorm_GeneralP_MatchesFormula()
    {
        // ||[1,2,3]||₃ = (1+8+27)^(1/3) = 36^(1/3) ≈ 3.30193
        ValueRef result = await InvokeAsync(new VecNormFunction(), F32(1f, 2f, 3f), ValueRef.FromFloat32(3f));
        Assert.Equal((float)System.Math.Pow(36.0, 1.0 / 3.0), (float)result.ToDouble(), 4);
    }

    [Fact]
    public async Task VecNorm_NonPositiveP_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new VecNormFunction(), F32(1f, 2f), ValueRef.FromFloat32(0f)));
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new VecNormFunction(), F32(1f, 2f), ValueRef.FromFloat32(-1f)));
    }

    [Fact]
    public async Task VecNorm_EmptyReturnsZero()
    {
        Assert.Equal(0f, (float)(await InvokeAsync(new VecNormFunction(), F32())).ToDouble(), 5);
    }

    // ─── vec_count_nonzero / vec_any / vec_all ───────────────────────────────

    [Fact]
    public async Task VecCountNonzero_CountsNonZero()
    {
        Assert.Equal(2f, (float)(await InvokeAsync(new VecCountNonzeroFunction(), F32(0f, 1f, 0f, -2f, 0f))).ToDouble(), 5);
        Assert.Equal(0f, (float)(await InvokeAsync(new VecCountNonzeroFunction(), F32())).ToDouble(), 5);
    }

    [Fact]
    public async Task VecAny_AndVecAll_Predicates()
    {
        Assert.Equal(1f, (float)(await InvokeAsync(new VecAnyFunction(), F32(0f, 0f, 1f))).ToDouble(), 5);
        Assert.Equal(0f, (float)(await InvokeAsync(new VecAnyFunction(), F32(0f, 0f, 0f))).ToDouble(), 5);
        Assert.Equal(0f, (float)(await InvokeAsync(new VecAnyFunction(), F32())).ToDouble(), 5);

        Assert.Equal(1f, (float)(await InvokeAsync(new VecAllFunction(), F32(1f, 2f, 3f))).ToDouble(), 5);
        Assert.Equal(0f, (float)(await InvokeAsync(new VecAllFunction(), F32(1f, 0f, 3f))).ToDouble(), 5);
        Assert.Equal(1f, (float)(await InvokeAsync(new VecAllFunction(), F32())).ToDouble(), 5);
    }

    // ─── argmin (and the vec_* alias coverage via argmax) ────────────────────

    [Fact]
    public async Task Argmin_PicksLowestIndexOfMinimum()
    {
        // [3, 1, 4, 1, 5] → first 1 is at index 2 (1-based)
        ValueRef result = await InvokeAsync(new ArgminFunction(), F32(3f, 1f, 4f, 1f, 5f));
        Assert.Equal(2, result.ToInt32());
    }

    [Fact]
    public async Task Argmin_EmptyThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new ArgminFunction(), F32()));
    }

    // ─── vec / vec_concat ────────────────────────────────────────────────────

    [Fact]
    public async Task Vec_FlattensMixedScalarAndArrayArguments()
    {
        // vec(1, [2,3], 4, [5]) → [1,2,3,4,5]
        ValueRef result = await InvokeAsync(new VecFunction(),
            ValueRef.FromFloat32(1f), F32(2f, 3f),
            ValueRef.FromFloat32(4f), F32(5f));
        float[] arr = AsFloatArr(result);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f }, arr);
    }

    [Fact]
    public async Task Vec_NullArgPropagates()
    {
        ValueRef result = await InvokeAsync(new VecFunction(),
            ValueRef.FromFloat32(1f), ValueRef.Null(DataKind.Float32));
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task VecConcat_JoinsVectorsInOrder()
    {
        ValueRef result = await InvokeAsync(new VecConcatFunction(),
            F32(1f, 2f), F32(3f, 4f, 5f));
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f }, AsFloatArr(result));
    }

    [Fact]
    public async Task VecConcat_NullArgPropagates()
    {
        ValueRef result = await InvokeAsync(new VecConcatFunction(),
            F32(1f, 2f), ValueRef.NullArray(DataKind.Float32));
        Assert.True(result.IsNull);
    }

    // ─── vec_reverse / vec_sort / vec_unique ─────────────────────────────────

    [Fact]
    public async Task VecReverse_FlipsOrder()
    {
        Assert.Equal(new[] { 3f, 2f, 1f },
            AsFloatArr(await InvokeAsync(new VecReverseFunction(), F32(1f, 2f, 3f))));
        Assert.Empty(AsFloatArr(await InvokeAsync(new VecReverseFunction(), F32())));
    }

    [Fact]
    public async Task VecSort_AscendingOrder()
    {
        Assert.Equal(new[] { -2f, 0f, 1f, 3f, 5f },
            AsFloatArr(await InvokeAsync(new VecSortFunction(), F32(3f, 1f, -2f, 5f, 0f))));
    }

    [Fact]
    public async Task VecUnique_PreservesFirstOccurrenceOrder()
    {
        // First-seen ordering: 3, 1, 4, 5, 9, 2, 6
        Assert.Equal(new[] { 3f, 1f, 4f, 5f, 9f, 2f, 6f },
            AsFloatArr(await InvokeAsync(new VecUniqueFunction(),
                F32(3f, 1f, 4f, 1f, 5f, 9f, 2f, 6f, 5f, 3f))));
    }

    // ─── vec_pad / vec_repeat ────────────────────────────────────────────────

    [Fact]
    public async Task VecPad_AppendsFillToReachLength()
    {
        Assert.Equal(new[] { 1f, 2f, 3f, -1f, -1f },
            AsFloatArr(await InvokeAsync(new VecPadFunction(),
                F32(1f, 2f, 3f), ValueRef.FromInt32(5), ValueRef.FromFloat32(-1f))));
    }

    [Fact]
    public async Task VecPad_LenAtMostSourceReturnsSourceUnchanged()
    {
        // No truncation: source length 3, request 2 → still [1,2,3].
        Assert.Equal(new[] { 1f, 2f, 3f },
            AsFloatArr(await InvokeAsync(new VecPadFunction(),
                F32(1f, 2f, 3f), ValueRef.FromInt32(2), ValueRef.FromFloat32(0f))));
    }

    [Fact]
    public async Task VecPad_NegativeLenThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new VecPadFunction(),
                F32(1f), ValueRef.FromInt32(-1), ValueRef.FromFloat32(0f)));
    }

    [Fact]
    public async Task VecRepeat_TilesVector()
    {
        Assert.Equal(new[] { 1f, 2f, 1f, 2f, 1f, 2f },
            AsFloatArr(await InvokeAsync(new VecRepeatFunction(),
                F32(1f, 2f), ValueRef.FromInt32(3))));
    }

    [Fact]
    public async Task VecRepeat_ZeroCountReturnsEmpty()
    {
        Assert.Empty(AsFloatArr(await InvokeAsync(new VecRepeatFunction(),
            F32(1f, 2f), ValueRef.FromInt32(0))));
    }

    [Fact]
    public async Task VecRepeat_NegativeCountThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new VecRepeatFunction(), F32(1f), ValueRef.FromInt32(-1)));
    }

    // ─── linspace / arange ──────────────────────────────────────────────────

    [Fact]
    public async Task Linspace_EvenlySpacedInclusiveEndpoints()
    {
        // linspace(0, 1, 5) = [0, 0.25, 0.5, 0.75, 1]
        float[] result = AsFloatArr(await InvokeAsync(new LinspaceFunction(),
            ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(5)));
        Assert.Equal(5, result.Length);
        Assert.Equal(0f, result[0], 5);
        Assert.Equal(0.25f, result[1], 5);
        Assert.Equal(0.5f, result[2], 5);
        Assert.Equal(0.75f, result[3], 5);
        Assert.Equal(1f, result[4], 5);
    }

    [Fact]
    public async Task Linspace_SinglePointReturnsStart()
    {
        float[] result = AsFloatArr(await InvokeAsync(new LinspaceFunction(),
            ValueRef.FromFloat32(7f), ValueRef.FromFloat32(99f), ValueRef.FromInt32(1)));
        Assert.Single(result);
        Assert.Equal(7f, result[0]);
    }

    [Fact]
    public async Task Linspace_ZeroPointsReturnsEmpty()
    {
        Assert.Empty(AsFloatArr(await InvokeAsync(new LinspaceFunction(),
            ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(0))));
    }

    [Fact]
    public async Task Linspace_NegativeNThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new LinspaceFunction(),
                ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromInt32(-1)));
    }

    [Fact]
    public async Task Arange_PositiveStepExcludesStop()
    {
        // arange(0, 5, 1) = [0,1,2,3,4]  (stop excluded)
        Assert.Equal(new[] { 0f, 1f, 2f, 3f, 4f },
            AsFloatArr(await InvokeAsync(new ArangeFunction(),
                ValueRef.FromFloat32(0f), ValueRef.FromFloat32(5f), ValueRef.FromFloat32(1f))));
    }

    [Fact]
    public async Task Arange_FractionalStep()
    {
        // arange(0, 1, 0.25) = [0, 0.25, 0.5, 0.75]
        float[] result = AsFloatArr(await InvokeAsync(new ArangeFunction(),
            ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f), ValueRef.FromFloat32(0.25f)));
        Assert.Equal(4, result.Length);
        Assert.Equal(0f, result[0], 5);
        Assert.Equal(0.25f, result[1], 5);
        Assert.Equal(0.5f, result[2], 5);
        Assert.Equal(0.75f, result[3], 5);
    }

    [Fact]
    public async Task Arange_NegativeStepDescends()
    {
        // arange(5, 0, -1) = [5,4,3,2,1]
        Assert.Equal(new[] { 5f, 4f, 3f, 2f, 1f },
            AsFloatArr(await InvokeAsync(new ArangeFunction(),
                ValueRef.FromFloat32(5f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(-1f))));
    }

    [Fact]
    public async Task Arange_StartPastStopReturnsEmpty()
    {
        // start >= stop with positive step → empty
        Assert.Empty(AsFloatArr(await InvokeAsync(new ArangeFunction(),
            ValueRef.FromFloat32(5f), ValueRef.FromFloat32(0f), ValueRef.FromFloat32(1f))));
    }

    [Fact]
    public async Task Arange_ZeroStepThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() =>
            InvokeAsync(new ArangeFunction(),
                ValueRef.FromFloat32(0f), ValueRef.FromFloat32(5f), ValueRef.FromFloat32(0f)));
    }

    // ─── nms ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nms_TwoOverlappingBoxes_KeepsHigherScoreOnly()
    {
        // Two boxes that overlap heavily.
        ValueRef boxes = F32(
            10f, 10f, 50f, 50f,   // box 0: (10,10)-(50,50)
            15f, 15f, 55f, 55f);  // box 1: (15,15)-(55,55) — large overlap with box 0
        ValueRef scores = F32(0.9f, 0.7f);
        ValueRef result = await InvokeAsync(new NmsFunction(), boxes, scores, ValueRef.FromFloat32(0.5f));

        int[] kept = AsIntArr(result);
        Assert.Equal([0], kept);
    }

    [Fact]
    public async Task Nms_NonOverlappingBoxes_KeepsAll()
    {
        ValueRef boxes = F32(
            0f, 0f, 10f, 10f,        // box 0
            20f, 20f, 30f, 30f,      // box 1
            40f, 40f, 50f, 50f);     // box 2
        ValueRef scores = F32(0.9f, 0.8f, 0.7f);
        ValueRef result = await InvokeAsync(new NmsFunction(), boxes, scores, ValueRef.FromFloat32(0.5f));

        int[] kept = AsIntArr(result);
        // All boxes survive (no overlap); order is score-descending = original index order.
        Assert.Equal([0, 1, 2], kept);
    }

    [Fact]
    public async Task Nms_InvalidIouThreshold_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new NmsFunction(),
            F32(0f, 0f, 10f, 10f),
            F32(0.9f),
            ValueRef.FromFloat32(1.5f)));
    }

    // ─── tensor_to_image_chw ─────────────────────────────────────────────────

    [Fact]
    public async Task TensorToImage_ReconstructsSolidColor()
    {
        // 1×1 RGB(128, 64, 200) → tensor at /255 → reconstruct via 3-arg form.
        float r = 128f / 255f, g = 64f / 255f, b = 200f / 255f;
        ValueRef result = await InvokeAsync(new TensorToImageChwFunction(),
            F32(r, g, b),
            ValueRef.FromInt32(1), ValueRef.FromInt32(1));

        Assert.Equal(DataKind.Image, result.Kind);
        using SKBitmap bmp = result.AsImage();
        SKColor px = bmp.GetPixel(0, 0);
        Assert.Equal(128, px.Red);
        Assert.Equal(64, px.Green);
        Assert.Equal(200, px.Blue);
    }

    [Fact]
    public async Task TensorToImage_RoundTripsImageToTensor()
    {
        // Build a 2×2 solid image, push through image_to_tensor_chw, then back
        // through tensor_to_image_chw. Pixels should round-trip within 1 byte.
        using SKBitmap solid = new(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas canvas = new(solid)) canvas.Clear(new SKColor(100, 200, 50));

        ValueRef forward = await InvokeAsync(new ImageToTensorChwFunction(),
            ValueRef.FromImage(solid),
            ValueRef.FromPrimitiveArray(new[] { 2, 2 }, DataKind.Int32));
        float[] tensor = AsFloatArr(forward);

        ValueRef back = await InvokeAsync(new TensorToImageChwFunction(),
            ValueRef.FromPrimitiveArray(tensor, DataKind.Float32),
            ValueRef.FromInt32(2), ValueRef.FromInt32(2));

        using SKBitmap reconstructed = back.AsImage();
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                SKColor px = reconstructed.GetPixel(x, y);
                Assert.InRange(px.Red - 100, -1, 1);
                Assert.InRange(px.Green - 200, -1, 1);
                Assert.InRange(px.Blue - 50, -1, 1);
            }
        }
    }

    [Fact]
    public async Task TensorToImage_WrongLength_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new TensorToImageChwFunction(),
            F32(1f, 2f, 3f, 4f),                            // length 4 ≠ 3*2*2=12
            ValueRef.FromInt32(2), ValueRef.FromInt32(2)));
    }

    // ─── mask_to_polygon ─────────────────────────────────────────────────────

    [Fact]
    public async Task MaskToPolygon_CenteredSquare_ReturnsRoughlyRectangularContour()
    {
        // 5×5 mask with a 3×3 centred filled square.
        // 0 0 0 0 0
        // 0 1 1 1 0
        // 0 1 1 1 0
        // 0 1 1 1 0
        // 0 0 0 0 0
        float[] mask = new float[25];
        for (int y = 1; y <= 3; y++)
            for (int x = 1; x <= 3; x++)
                mask[y * 5 + x] = 1f;

        ValueRef result = await InvokeAsync(new MaskToPolygonFunction(),
            ValueRef.FromPrimitiveArray(mask, DataKind.Float32),
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f));

        Vector2[] points = (Vector2[])result.Materialized!;
        Assert.True(points.Length >= 3, $"Expected at least 3 vertices for a closed polygon, got {points.Length}.");

        // All vertices should land near the iso-contour at half-pixel inset
        // from the 3×3 box's corners — i.e. roughly between x∈[0.5, 3.5]
        // and y∈[0.5, 3.5].
        foreach (Vector2 p in points)
        {
            Assert.InRange(p.X, 0.4f, 3.6f);
            Assert.InRange(p.Y, 0.4f, 3.6f);
        }

        // Vertices should span the full box range — extremes near 0.5 and 3.5.
        float minX = points[0].X, maxX = points[0].X;
        float minY = points[0].Y, maxY = points[0].Y;
        foreach (Vector2 p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        Assert.InRange(minX, 0.4f, 0.6f);
        Assert.InRange(maxX, 3.4f, 3.6f);
        Assert.InRange(minY, 0.4f, 0.6f);
        Assert.InRange(maxY, 3.4f, 3.6f);
    }

    [Fact]
    public async Task MaskToPolygon_EmptyMask_ReturnsEmptyArray()
    {
        float[] mask = new float[25];   // all zeros
        ValueRef result = await InvokeAsync(new MaskToPolygonFunction(),
            ValueRef.FromPrimitiveArray(mask, DataKind.Float32),
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f));

        Vector2[] points = (Vector2[])result.Materialized!;
        Assert.Empty(points);
    }

    [Fact]
    public async Task MaskToPolygon_WrongLength_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => InvokeAsync(
            new MaskToPolygonFunction(),
            F32(1f, 2f, 3f, 4f),                            // length 4 ≠ 5*5
            ValueRef.FromInt32(5), ValueRef.FromInt32(5),
            ValueRef.FromFloat32(0.5f)));
    }
}
