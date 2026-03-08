using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Covers <c>yolox_preprocess</c> + <c>yolox_postprocess</c> — the two
/// model-specific scalars the SQL-defined YOLOX model bodies call. Tests
/// don't require a real ONNX file; they exercise the preprocessing math
/// (letterbox + BGR + raw 0-255 pack) and the postprocessing math
/// (decoder + class-aware NMS + reverse letterbox) against hand-crafted
/// inputs.
/// </summary>
public sealed class YoloxFunctionsTests
{
    private static SKBitmap SolidBitmap(int width, int height, byte r, byte g, byte b)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(new SKColor(r, g, b));
        return bmp;
    }

    private static EvaluationFrame Frame() =>
        new(Row.Empty, new Arena(), new Arena(), types: new TypeRegistry());

    // ─── yolox_preprocess ────────────────────────────────────────────────────

    [Fact]
    public async Task Preprocess_SquareImage_ProducesNchwBgrRaw()
    {
        // Square 64×64 of RGB=(10, 200, 50). Letterbox at 64 → no padding;
        // ratio = 1.0; every output pixel is (10, 200, 50) BGR-packed.
        using SKBitmap bmp = SolidBitmap(64, 64, 10, 200, 50);

        YoloxPreprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(bmp), ValueRef.FromInt32(64) }.AsMemory(),
            Frame(), CancellationToken.None);

        Assert.True(result.IsArray);
        float[] data = (float[])result.Materialized!;
        Assert.Equal(3 * 64 * 64, data.Length);

        // NCHW + BGR: plane 0 = B (50), plane 1 = G (200), plane 2 = R (10).
        // Raw 0-255 — no normalization.
        const int planeSize = 64 * 64;
        Assert.Equal(50f, data[0]);
        Assert.Equal(50f, data[planeSize - 1]);
        Assert.Equal(200f, data[planeSize]);
        Assert.Equal(200f, data[2 * planeSize - 1]);
        Assert.Equal(10f, data[2 * planeSize]);
        Assert.Equal(10f, data[3 * planeSize - 1]);
    }

    [Fact]
    public async Task Preprocess_WideImage_PadsBottomWith114()
    {
        // 80×40 image, target 80. Image lands in top-left of 80×80 canvas;
        // bottom 40 rows are pad-fill 114.
        using SKBitmap bmp = SolidBitmap(80, 40, 0, 0, 0);

        YoloxPreprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(bmp), ValueRef.FromInt32(80) }.AsMemory(),
            Frame(), CancellationToken.None);

        float[] data = (float[])result.Materialized!;
        const int planeSize = 80 * 80;

        // Plane 0 (B): rows 0..39 are 0 (image), rows 40..79 are 114 (pad).
        Assert.Equal(0f, data[39 * 80]);    // last image row, first col
        Assert.Equal(114f, data[40 * 80]);  // first pad row
        Assert.Equal(114f, data[planeSize - 1]); // last cell in plane

        // Plane 2 (R): same shape since image is all zeros.
        Assert.Equal(0f, data[2 * planeSize + 39 * 80]);
        Assert.Equal(114f, data[2 * planeSize + 40 * 80]);
    }

    [Fact]
    public async Task Preprocess_NullImage_ReturnsNullArray()
    {
        YoloxPreprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromInt32(640) }.AsMemory(),
            Frame(), CancellationToken.None);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Preprocess_BadTargetSize_ThrowsFunctionArgumentException()
    {
        using SKBitmap bmp = SolidBitmap(10, 10, 0, 0, 0);
        YoloxPreprocessFunction fn = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[] { ValueRef.FromImage(bmp), ValueRef.FromInt32(0) }.AsMemory(),
                Frame(), CancellationToken.None));
    }

    // ─── yolox_postprocess ───────────────────────────────────────────────────

    [Fact]
    public async Task Postprocess_BadRawLength_ThrowsFunctionArgumentException()
    {
        using SKBitmap bmp = SolidBitmap(640, 480, 0, 0, 0);
        YoloxPostprocessFunction fn = new();
        ValueRef[] labels = [ValueRef.FromString("person"), ValueRef.FromString("car")];

        // Wrong raw tensor length. input_size=640 expects 8400 anchors × 85 = 714000 floats.
        float[] tooShort = new float[100];

        await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await fn.ExecuteAsync(
                new ValueRef[]
                {
                    ValueRef.FromPrimitiveArray(tooShort, DataKind.Float32),
                    ValueRef.FromArray(DataKind.String, labels),
                    ValueRef.FromImage(bmp),
                    ValueRef.FromInt32(640),
                    ValueRef.FromFloat32(0.25f),
                    ValueRef.FromFloat32(0.45f),
                }.AsMemory(),
                Frame(), CancellationToken.None));
    }

    [Fact]
    public async Task Postprocess_AllSubThreshold_ReturnsEmptyArray()
    {
        // 416 input → 3549 anchors × 85 floats. All zeros means objectness=0
        // and class_scores=0; confidence = 0 × 0 = 0 < threshold → all dropped.
        using SKBitmap bmp = SolidBitmap(416, 416, 0, 0, 0);
        ValueRef[] labels = new ValueRef[80];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = ValueRef.FromString($"class_{i}");
        }

        int anchorCount = (416 / 8) * (416 / 8) + (416 / 16) * (416 / 16) + (416 / 32) * (416 / 32);
        float[] raw = new float[anchorCount * 85];

        YoloxPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(raw, DataKind.Float32),
                ValueRef.FromArray(DataKind.String, labels),
                ValueRef.FromImage(bmp),
                ValueRef.FromInt32(416),
                ValueRef.FromFloat32(0.25f),
                ValueRef.FromFloat32(0.45f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        Assert.True(result.IsArray);
        Assert.Equal(0, result.GetArrayElements().Length);
    }

    [Fact]
    public async Task Postprocess_SingleHotAnchor_EmitsOneLabeledDetection()
    {
        // 416 input. Plant one high-confidence anchor in the middle of the
        // stride-32 region; expect one detection back with the right label
        // and a sensible bbox.
        using SKBitmap bmp = SolidBitmap(416, 416, 0, 0, 0);
        ValueRef[] labels = new ValueRef[80];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = ValueRef.FromString($"class_{i}");
        }

        // Layout: stride-8 (52×52=2704 anchors), stride-16 (26×26=676),
        // stride-32 (13×13=169). Total = 3549.
        int anchorCount = 52 * 52 + 26 * 26 + 13 * 13;
        Assert.Equal(3549, anchorCount); // sanity
        float[] raw = new float[anchorCount * 85];

        // Put a hot anchor at the very first stride-32 anchor (index 2704+676=3380).
        // gridX=0, gridY=0, stride=32 → cx=0, cy=0, bw=exp(0)*32=32, bh=32.
        // objectness=1.0, class[7]=1.0 → confidence=1.0.
        int hot = 52 * 52 + 26 * 26;
        Assert.Equal(3380, hot);
        int hotBase = hot * 85;
        raw[hotBase + 0] = 0f;       // raw cx offset
        raw[hotBase + 1] = 0f;       // raw cy offset
        raw[hotBase + 2] = 0f;       // log(w/stride) = 0 → w = stride
        raw[hotBase + 3] = 0f;       // log(h/stride) = 0 → h = stride
        raw[hotBase + 4] = 1.0f;     // objectness
        raw[hotBase + 5 + 7] = 1.0f; // class 7 = "class_7"

        YoloxPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(raw, DataKind.Float32),
                ValueRef.FromArray(DataKind.String, labels),
                ValueRef.FromImage(bmp),
                ValueRef.FromInt32(416),
                ValueRef.FromFloat32(0.25f),
                ValueRef.FromFloat32(0.45f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        Assert.True(result.IsArray);
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(1, elements.Length);

        // Pull out the LabeledDetection struct.
        ValueRef detection = elements[0];
        Assert.Equal(DataKind.Struct, detection.Kind);

        // ratio = min(416/416, 416/416) = 1.0 → coords match the input-size space.
        // cx=0, cy=0, bw=32, bh=32 → x = (0 - 16) / 1 = -16, y = -16, w = 32, h = 32.
        // (Negative x/y is fine — the box extends outside the image; clipping
        // is the user's responsibility downstream.)
        ReadOnlySpan<ValueRef> fields = detection.GetStructFields();
        Assert.Equal(3, fields.Length);

        // Field 0: bbox struct
        ValueRef bbox = fields[0];
        Assert.Equal(DataKind.Struct, bbox.Kind);
        ReadOnlySpan<ValueRef> bboxFields = bbox.GetStructFields();
        Assert.Equal(4, bboxFields.Length);
        Assert.Equal(-16f, bboxFields[0].AsFloat32(), 2);  // x
        Assert.Equal(-16f, bboxFields[1].AsFloat32(), 2);  // y
        Assert.Equal(32f,  bboxFields[2].AsFloat32(), 2);  // w
        Assert.Equal(32f,  bboxFields[3].AsFloat32(), 2);  // h

        // Field 1: label string
        Assert.Equal("class_7", fields[1].AsString());

        // Field 2: score
        Assert.Equal(1.0f, fields[2].AsFloat32(), 4);
    }

    [Fact]
    public async Task Postprocess_LetterboxRatio_ReversesCorrectly()
    {
        // Non-square image: 832×416 (2:1). Letterbox at 416 → ratio = 0.5.
        // Same single-hot-anchor setup as before, but coords should scale back
        // by /0.5 = ×2 to map to the original-image space.
        using SKBitmap bmp = SolidBitmap(832, 416, 0, 0, 0);
        ValueRef[] labels = new ValueRef[80];
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = ValueRef.FromString($"c{i}");
        }

        int anchorCount = 52 * 52 + 26 * 26 + 13 * 13;
        float[] raw = new float[anchorCount * 85];
        int hot = 52 * 52 + 26 * 26;
        int hotBase = hot * 85;
        raw[hotBase + 4] = 1f;
        raw[hotBase + 5] = 1f;

        YoloxPostprocessFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[]
            {
                ValueRef.FromPrimitiveArray(raw, DataKind.Float32),
                ValueRef.FromArray(DataKind.String, labels),
                ValueRef.FromImage(bmp),
                ValueRef.FromInt32(416),
                ValueRef.FromFloat32(0.25f),
                ValueRef.FromFloat32(0.45f),
            }.AsMemory(),
            Frame(), CancellationToken.None);

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(1, elements.Length);
        ReadOnlySpan<ValueRef> fields = elements[0].GetStructFields();
        ReadOnlySpan<ValueRef> bboxFields = fields[0].GetStructFields();

        // ratio = min(416/832, 416/416) = 0.5. cx=0, cy=0, bw=bh=32 →
        // x = (0 - 16) / 0.5 = -32, y = -32, w = 64, h = 64.
        Assert.Equal(-32f, bboxFields[0].AsFloat32(), 2);
        Assert.Equal(-32f, bboxFields[1].AsFloat32(), 2);
        Assert.Equal(64f,  bboxFields[2].AsFloat32(), 2);
        Assert.Equal(64f,  bboxFields[3].AsFloat32(), 2);
    }
}
