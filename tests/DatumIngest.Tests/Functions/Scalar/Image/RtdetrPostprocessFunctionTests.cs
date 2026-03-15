using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Unit tests for <c>rtdetr_postprocess</c>: sigmoid + per-query argmax +
/// confidence filter + box denormalization. Doesn't require a real ONNX
/// file — fabricated logits/boxes feed the decoder directly so we can
/// pin exact output bounds, labels, and scores.
/// </summary>
public sealed class RtdetrPostprocessFunctionTests
{
    private static SKBitmap SolidBitmap(int width, int height) =>
        new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    private static EvaluationFrame Frame() =>
        new(Row.Empty, new Arena(), new Arena(), new MemoryAccountant(), types: new TypeRegistry());

    private static ValueRef MakeLabels(int count)
    {
        ValueRef[] labels = new ValueRef[count];
        for (int i = 0; i < count; i++) labels[i] = ValueRef.FromString($"class_{i}");
        return ValueRef.FromArray(DataKind.String, labels);
    }

    /// <summary>
    /// Builds a logits buffer of shape <c>[numQueries, numClasses]</c> where
    /// every entry is the low-confidence baseline except for the
    /// <paramref name="hotQuery"/>'th query, where class <paramref name="hotClass"/>
    /// is set high (logit 10 ≈ sigmoid 0.9999).
    /// </summary>
    private static float[] MakeLogits(int numQueries, int numClasses, int hotQuery, int hotClass)
    {
        float[] logits = new float[numQueries * numClasses];
        // Baseline: -10 → sigmoid ≈ 4.5e-5 (well below any sane threshold).
        for (int i = 0; i < logits.Length; i++) logits[i] = -10f;
        logits[hotQuery * numClasses + hotClass] = 10f;
        return logits;
    }

    /// <summary>
    /// Builds a per-query box buffer of shape <c>[numQueries, 4]</c> in
    /// normalized <c>[cx, cy, w, h]</c> with every box pointing at the
    /// image center at <paramref name="size"/> normalized w/h. Sets a
    /// distinct box at <paramref name="hotQuery"/>.
    /// </summary>
    private static float[] MakeBoxes(int numQueries, int hotQuery, float cx, float cy, float w, float h)
    {
        float[] boxes = new float[numQueries * 4];
        // Filler boxes (won't matter — corresponding queries are sub-threshold).
        for (int i = 0; i < numQueries; i++)
        {
            boxes[i * 4 + 0] = 0.5f;
            boxes[i * 4 + 1] = 0.5f;
            boxes[i * 4 + 2] = 0.01f;
            boxes[i * 4 + 3] = 0.01f;
        }
        boxes[hotQuery * 4 + 0] = cx;
        boxes[hotQuery * 4 + 1] = cy;
        boxes[hotQuery * 4 + 2] = w;
        boxes[hotQuery * 4 + 3] = h;
        return boxes;
    }

    [Fact]
    public async Task SingleHotQuery_EmitsOneDetection_WithCorrectLabelAndPixelBox()
    {
        // 200×100 image. The hot query (#5) has class=3 with logit=10 (sigmoid≈1)
        // and a box at (cx=0.5, cy=0.5, w=0.4, h=0.4) normalized.
        // Expected pixel bbox: cx_px = 0.5*200 = 100, cy_px = 0.5*100 = 50,
        // bw_px = 0.4*200 = 80, bh_px = 0.4*100 = 40.
        // Top-left: x = 100 - 40 = 60, y = 50 - 20 = 30, w = 80, h = 40.
        using SKBitmap img = SolidBitmap(200, 100);

        float[] logits = MakeLogits(numQueries: 10, numClasses: 80, hotQuery: 5, hotClass: 3);
        float[] boxes = MakeBoxes(numQueries: 10, hotQuery: 5, cx: 0.5f, cy: 0.5f, w: 0.4f, h: 0.4f);

        RtdetrPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                ValueRef.FromPrimitiveArray(boxes, DataKind.Float32),
                MakeLabels(80),
                ValueRef.FromImage(img),
                ValueRef.FromFloat32(0.5f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        Assert.True(result.IsArray);
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(1, elements.Length);

        ValueRef detection = elements[0];
        ReadOnlySpan<ValueRef> fields = detection.GetStructFields();
        Assert.Equal(3, fields.Length);

        ReadOnlySpan<ValueRef> bboxFields = fields[0].GetStructFields();
        Assert.Equal(60f, bboxFields[0].AsFloat32(), 2); // x
        Assert.Equal(30f, bboxFields[1].AsFloat32(), 2); // y
        Assert.Equal(80f, bboxFields[2].AsFloat32(), 2); // w
        Assert.Equal(40f, bboxFields[3].AsFloat32(), 2); // h

        Assert.Equal("class_3", fields[1].AsString());
        // Score is sigmoid(10) ≈ 0.99995. Within 0.001.
        Assert.True(fields[2].AsFloat32() > 0.999f);
    }

    [Fact]
    public async Task ConfThresh_FiltersBelowThreshold()
    {
        // All queries baseline logits → max sigmoid is tiny → zero detections
        // at threshold 0.5.
        using SKBitmap img = SolidBitmap(100, 100);
        float[] logits = new float[10 * 80];
        for (int i = 0; i < logits.Length; i++) logits[i] = -5f; // sigmoid ≈ 0.0067
        float[] boxes = MakeBoxes(numQueries: 10, hotQuery: 0, 0.5f, 0.5f, 0.1f, 0.1f);

        RtdetrPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                ValueRef.FromPrimitiveArray(boxes, DataKind.Float32),
                MakeLabels(80),
                ValueRef.FromImage(img),
                ValueRef.FromFloat32(0.5f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        Assert.True(result.IsArray);
        Assert.Equal(0, result.GetArrayElements().Length);
    }

    [Fact]
    public async Task LabelsLengthDerivesNumClasses()
    {
        // 20 queries × 5 classes = 100 logits. Labels length determines
        // num_classes; logits.Length / num_classes determines num_queries.
        using SKBitmap img = SolidBitmap(100, 100);
        int numQueries = 20, numClasses = 5;
        float[] logits = MakeLogits(numQueries, numClasses, hotQuery: 12, hotClass: 2);
        float[] boxes = MakeBoxes(numQueries, hotQuery: 12, 0.5f, 0.5f, 0.2f, 0.2f);

        RtdetrPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                ValueRef.FromPrimitiveArray(boxes, DataKind.Float32),
                MakeLabels(numClasses),
                ValueRef.FromImage(img),
                ValueRef.FromFloat32(0.5f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(1, elements.Length);
        Assert.Equal("class_2", elements[0].GetStructFields()[1].AsString());
    }

    [Fact]
    public async Task LogitsLengthIndivisibleByNumClasses_Throws()
    {
        using SKBitmap img = SolidBitmap(100, 100);
        // 7 floats with 5-class labels → 7 % 5 != 0 → bail.
        float[] logits = new float[7];
        float[] boxes = new float[4];
        RtdetrPostprocessFunction fn = new();
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
        {
            await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                    ValueRef.FromPrimitiveArray(boxes, DataKind.Float32),
                    MakeLabels(5),
                    ValueRef.FromImage(img),
                    ValueRef.FromFloat32(0.5f),
                }.AsMemory(),
                Frame(), CancellationToken.None);
        });
        Assert.Contains("not divisible", ex.Message);
    }

    [Fact]
    public async Task BoxesLengthMismatch_Throws()
    {
        using SKBitmap img = SolidBitmap(100, 100);
        // 10 queries × 5 classes = 50 logits; boxes should be 40 floats (10×4).
        // Pass 30 → mismatch.
        float[] logits = new float[10 * 5];
        float[] boxes = new float[30];
        RtdetrPostprocessFunction fn = new();
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
        {
            await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromPrimitiveArray(logits, DataKind.Float32),
                    ValueRef.FromPrimitiveArray(boxes, DataKind.Float32),
                    MakeLabels(5),
                    ValueRef.FromImage(img),
                    ValueRef.FromFloat32(0.5f),
                }.AsMemory(),
                Frame(), CancellationToken.None);
        });
        Assert.Contains("boxes length", ex.Message);
    }

    [Fact]
    public async Task NullLogits_ReturnsEmptyArray()
    {
        // Mirror yolox_postprocess's "null → no detections" behaviour so
        // NULL-propagating chains downstream stay clean.
        using SKBitmap img = SolidBitmap(100, 100);
        RtdetrPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.NullArray(DataKind.Float32),
                ValueRef.FromPrimitiveArray(new float[4], DataKind.Float32),
                MakeLabels(80),
                ValueRef.FromImage(img),
                ValueRef.FromFloat32(0.5f),
            }.AsMemory(),
            Frame(), CancellationToken.None);
        Assert.True(result.IsArray);
        Assert.Equal(0, result.GetArrayElements().Length);
    }
}
