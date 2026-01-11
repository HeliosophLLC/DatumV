namespace DatumIngest.Tests.Models.Onnx;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;
using DatumIngest.Pooling;

/// <summary>
/// Smoke tests for the PP-OCRv4 text detector. Self-skip when the
/// model file is absent.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class PpOcrDetectionModelTests : ServiceTestBase
{
    private static string ModelPath => Path.Combine(
        ModelCatalog.DefaultModelDirectory, BuiltinModels.PpOcrDetV4DefaultFilename);

    private static bool ModelAvailable => File.Exists(ModelPath);

    private static string ReceiptFixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "000.jpg");

    [Fact]
    public void Load_RealPpOcrDet_ExposesExpectedSignature()
    {
        if (!ModelAvailable) return;

        using PpOcrDetectionModel model = new(name: "ppocr_det_v4", modelFilePath: ModelPath);

        Assert.Equal("ppocr_det_v4", model.Name);
        Assert.True(model.IsDeterministic);
        Assert.Equal(DataKind.Struct, model.OutputKind);
        Assert.Single(model.InputKinds);
        Assert.Equal(DataKind.Image, model.InputKinds[0]);

        // Output struct shape — leading (label, score, x, y, w, h)
        // matches SCRFD / YOLOX so detector consumers can be polymorphic.
        Assert.NotNull(model.OutputFields);
        Assert.Equal(6, model.OutputFields!.Count);
        Assert.Equal("label", model.OutputFields[0].Name);
        Assert.Equal("score", model.OutputFields[1].Name);
        Assert.Equal("x", model.OutputFields[2].Name);
        Assert.Equal("y", model.OutputFields[3].Name);
        Assert.Equal("w", model.OutputFields[4].Name);
        Assert.Equal("h", model.OutputFields[5].Name);
    }

    /// <summary>
    /// End-to-end on the bundled receipt fixture: the receipt has many
    /// printed text lines, so we expect at least a handful of boxes.
    /// All boxes should fit inside the image and have positive area.
    /// </summary>
    [Fact]
    public async Task InferBatch_RealReceiptFixture_FindsMultipleTextBoxes()
    {
        if (!ModelAvailable) return;
        if (!File.Exists(ReceiptFixturePath)) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using PpOcrDetectionModel model = new(name: "ppocr_det_v4", modelFilePath: ModelPath);

            byte[] jpg = await File.ReadAllBytesAsync(ReceiptFixturePath);
            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, jpg)]];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = Assert.Single(outputs);
            Assert.False(result.IsNull);
            Assert.True(result.IsArray);

            // Decode original image dims so we can assert each box is in bounds.
            using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(jpg);
            int origW = bitmap.Width;
            int origH = bitmap.Height;

            ReadOnlySpan<DatumIngest.Functions.ValueRef> boxes = result.GetArrayElements();
            Assert.True(boxes.Length >= 5,
                $"Expected ≥5 text boxes on the receipt fixture; got {boxes.Length}.");

            for (int i = 0; i < boxes.Length; i++)
            {
                DatumIngest.Functions.ValueRef det = boxes[i];
                Assert.False(det.IsNull);

                // Field order matches OutputFields: label, score, x, y, w, h.
                ReadOnlySpan<DatumIngest.Functions.ValueRef> fields = det.GetStructFields();
                string label = fields[0].AsString();
                float score = fields[1].AsFloat32();
                float x = fields[2].AsFloat32();
                float y = fields[3].AsFloat32();
                float w = fields[4].AsFloat32();
                float h = fields[5].AsFloat32();

                Assert.Equal("text", label);
                Assert.InRange(score, 0f, 1f);
                Assert.InRange(x, 0f, origW);
                Assert.InRange(y, 0f, origH);
                Assert.True(w > 0, $"box {i} has non-positive width {w}");
                Assert.True(h > 0, $"box {i} has non-positive height {h}");
                Assert.InRange(x + w, 0f, origW + 1f);
                Assert.InRange(y + h, 0f, origH + 1f);
            }
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    /// <summary>
    /// A near-blank image should produce zero (or very few) detections —
    /// confirms the threshold defaults aren't false-firing on noise.
    /// </summary>
    [Fact]
    public async Task InferBatch_BlankImage_ReturnsEmptyOrTinyArray()
    {
        if (!ModelAvailable) return;

        Pool pool = GetService<Pool>();
        Arena inputArena = pool.Backing.RentArena();
        Arena targetArena = pool.Backing.RentArena();

        try
        {
            using PpOcrDetectionModel model = new(name: "ppocr_det_v4", modelFilePath: ModelPath);

            using SkiaSharp.SKBitmap bitmap = new(640, 320, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
            bitmap.Erase(SkiaSharp.SKColors.White);
            using SkiaSharp.SKData encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            byte[] png = encoded.ToArray();

            DatumIngest.Functions.ValueRef[][] inputs =
                [[DatumIngest.Functions.ValueRef.FromBytes(DataKind.Image, png)]];

            IReadOnlyList<DatumIngest.Functions.ValueRef> outputs = await model.InferBatchAsync(
                inputs, overrides: [], cancellationToken: CancellationToken.None);

            DatumIngest.Functions.ValueRef result = outputs[0];
            // Blank canvases sometimes elicit one or two phantom detections at
            // the edges; allow up to a handful but not a forest.
            int boxCount = result.GetArrayElements().Length;
            Assert.True(boxCount <= 3,
                $"Blank image produced {boxCount} detections; expected ≤3.");
        }
        finally
        {
            pool.Backing.TryReturn(inputArena);
            pool.Backing.TryReturn(targetArena);
        }
    }

    [Fact]
    public void Catalog_RegisterAndResolve_YieldsPpOcrDetectionModel()
    {
        if (!ModelAvailable) return;

        ModelCatalog catalog = new(modelDirectory: ModelCatalog.DefaultModelDirectory);
        BuiltinModels.RegisterPpOcrDetV4(catalog);

        ModelCatalogEntry? entry = catalog.TryGetEntry("ppocr_det_v4");
        Assert.NotNull(entry);
        Assert.Equal("onnx", entry!.Backend);
        Assert.Equal(BuiltinModels.PpOcrDetV4DefaultFilename, entry.RelativePath);
        Assert.Equal("detector", entry.Category);

        using ModelLease lease = catalog.ResolveLeaseSynchronously("ppocr_det_v4");
        Assert.IsType<PpOcrDetectionModel>(lease.Model);
    }
}
