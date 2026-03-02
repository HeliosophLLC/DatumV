using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;
using DatumIngest.Models.Onnx;

using Microsoft.Extensions.Logging.Abstractions;

using SkiaSharp;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// End-to-end tests for a SQL-defined PP-OCRv4-det model: the four new
/// preprocessing/post-processing functions plus the CREATE MODEL plan
/// shape that ties them together. The deletion-candidate test for step 4 —
/// when this passes, <c>Models/Onnx/PpOcrDetectionModel.cs</c> can come
/// out and the SQL registration is the canonical source.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Model file.</strong> Downloaded on demand via
/// <see cref="ServiceTestBase.EnsureModelDownloadedAsync"/> from the
/// catalog entry's HuggingFace source. Lands at
/// <c>&lt;ModelsDirectory&gt;/paddleocr-v4-det/ch_PP-OCRv4_det.onnx</c>;
/// <c>ModelsDirectory</c> defaults to the per-user fallback or
/// <c>DATUM_MODELS</c> env-var override. Skipped when offline / the
/// download fails so CI without internet stays green.
/// </para>
/// <para>
/// <strong>Test strategy.</strong> Two layers:
/// <list type="number">
///   <item><c>PpOcrDet_CreateModelStatement_ParsesAndLowers</c> — confirms
///   the CREATE MODEL body parses, plans, and lowers into a Project +
///   Infer + Project chain (no MIO). Locks in the syntax and the lowering
///   pass.</item>
///   <item><c>PpOcrDet_SqlFunctionChain_MatchesCSharpReference</c> —
///   invokes the SQL function chain step-by-step against an in-process
///   SKBitmap, feeds the result tensor through the ONNX session via
///   <c>InferenceDispatcher</c>, runs <c>dbnet_postprocess</c>, and
///   compares boxes against the C# <see cref="PpOcrDetectionModel"/>.
///   Sidesteps the in-memory-table-provider's DataValue-arena friction;
///   exercises the same code paths the lowered plan would.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class PpOcrDetSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "paddleocr-v4-det";
    private const string OnnxFileName = "ch_PP-OCRv4_det.onnx";

    /// <summary>
    /// Best-effort: returns the absolute path to the model's ONNX file,
    /// downloading from HuggingFace if it isn't already on disk. Two-stage:
    /// <list type="number">
    ///   <item>Local fast path — if the file exists at the catalog-conventional
    ///         location, return it immediately. No network round-trip.</item>
    ///   <item>Slow path — call <see cref="ServiceTestBase.EnsureModelDownloadedAsync"/>
    ///         which probes via HF tree and downloads on miss. Returns null
    ///         on any failure (offline CI, gated repo, partial bytes) so the
    ///         caller can soft-skip.</item>
    /// </list>
    /// </summary>
    private async Task<string?> TryEnsureModelAvailableAsync()
    {
        string onnxPath = GetDownloadedModelPath(ModelId, OnnxFileName);
        if (File.Exists(onnxPath))
        {
            return onnxPath; // already downloaded — skip the probe entirely
        }

        try
        {
            await EnsureModelDownloadedAsync(ModelId);
        }
        catch
        {
            // Soft-skip: no network, HF gated repo, partial bytes that
            // need manual cleanup, etc. The test silently exits.
            return null;
        }

        return File.Exists(onnxPath) ? onnxPath : null;
    }

    /// <summary>
    /// Resolves the canonical SQL file via the manifest store — same lookup
    /// the front-end installer uses (catalog.json's <c>installSql</c> field).
    /// The path is relative to <see cref="IManifestStore.ManifestDirectory"/>
    /// (the directory containing catalog.json). Production install and
    /// test stay in lockstep through this one indirection.
    /// </summary>
    private string LoadCanonicalSql()
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogModel model = store.Manifest.Models.First(m => m.Id == ModelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{ModelId}' has no installSql; can't run the SQL E2E test.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    /// <summary>
    /// Draws a deterministic synthetic image with several distinct text
    /// regions. Glyph rendering depends on the host's installed fonts but
    /// is consistent within one process — both implementations see byte-
    /// identical input.
    /// </summary>
    private static SKBitmap MakeSyntheticImage(int width = 400, int height = 200)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(SKColors.White);

        using SKPaint paint = new()
        {
            Color = SKColors.Black,
            IsAntialias = true,
        };
        using SKFont font = new(SKTypeface.Default, 28);

        canvas.DrawText("Hello World", 20, 50, SKTextAlign.Left, font, paint);
        canvas.DrawText("Total $42.00", 20, 100, SKTextAlign.Left, font, paint);
        canvas.DrawText("ITEM 1", 20, 150, SKTextAlign.Left, font, paint);

        return bmp;
    }

    /// <summary>
    /// Step 1: CREATE MODEL with the PP-OCR-det body plans without error
    /// and lowers into the column-pipeline shape. Locks in syntax + step-3
    /// lowering for this body. Doesn't execute the plan — the table-
    /// provider's DataValue-arena friction is exercised by the second
    /// test below, which builds the data path manually.
    /// </summary>
    [Fact]
    public async Task PpOcrDet_CreateModelStatement_ParsesAndLowers()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        // SQL file ships in the repo at models/sql/paddleocr-v4-det.sql.
        // Its relative USING path resolves against catalog.Models.ModelDirectory,
        // which the downloader populated above.
        catalog.Plan(LoadCanonicalSql());

        // Verify the descriptor landed in the registry.
        Assert.True(
            catalog.DeclaredModels.TryGet(new QualifiedName("models", "paddleocr_v4_det"), out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(6, descriptor!.StatementBody.Count); // 5 DECLAREs + 1 RETURN
        Assert.True(ModelBodyLowerer.BodyIsStraightLine(descriptor.StatementBody),
            "PP-OCR-det body should be straight-line; step 3's lowerer must take it.");
    }

    /// <summary>
    /// Step 2: equivalence between the SQL function chain and the C#
    /// reference. Invokes <see cref="ImageResizeToStrideFunction"/>,
    /// <see cref="ImageToTensorChwFunction"/>, the ONNX session, and
    /// <see cref="DbnetPostprocessFunction"/> in the same order the
    /// lowered model body would, then compares against
    /// <see cref="PpOcrDetectionModel"/>.
    /// </summary>
    [Fact]
    public async Task PpOcrDet_SqlFunctionChain_MatchesCSharpReference()
    {
        string? onnxPath = await TryEnsureModelAvailableAsync();
        if (onnxPath is null) return;

        using SKBitmap testImage = MakeSyntheticImage();

        // === C# reference ===
        using PpOcrDetectionModel reference = new("ppocr_det_ref", onnxPath);
        IReadOnlyList<ValueRef> cSharpResults = await reference.InferBatchAsync(
            [[ValueRef.FromImage(testImage)]],
            [[]],
            CancellationToken.None);

        Assert.Single(cSharpResults);
        ValueRef cSharpOutput = cSharpResults[0];
        Assert.True(cSharpOutput.IsArray);

        DetectionRecord[] referenceDetections = ExtractDetections(cSharpOutput);

        // === SQL function chain — same order the lowered body would run ===
        EvaluationFrame frame = MakeFrame();
        ValueRef img = ValueRef.FromImage(testImage);

        // resized = image_resize_to_stride(img, 960, 32)
        ImageResizeToStrideFunction resizeFn = new();
        ValueRef resized = await resizeFn.ExecuteAsync(
            new[] { img, ValueRef.FromInt32(960), ValueRef.FromInt32(32) }.AsMemory(),
            frame, CancellationToken.None);

        // rh = image_height(resized); rw = image_width(resized)
        ImageHeightFunction heightFn = new();
        ImageWidthFunction widthFn = new();
        ValueRef rh = await heightFn.ExecuteAsync(new[] { resized }.AsMemory(), frame, CancellationToken.None);
        ValueRef rw = await widthFn.ExecuteAsync(new[] { resized }.AsMemory(), frame, CancellationToken.None);

        // tensor = image_to_tensor_chw(resized, [rh, rw], imagenet_mean(), imagenet_std())
        ValueRef tensor = await new ImageToTensorChwFunction().ExecuteAsync(
            new[]
            {
                resized,
                ValueRef.FromPrimitiveArray(new[] { rh.AsInt32(), rw.AsInt32() }, DataKind.Int32),
                ValueRef.FromPrimitiveArray(ImageNetMean(), DataKind.Float32),
                ValueRef.FromPrimitiveArray(ImageNetStd(), DataKind.Float32),
            }.AsMemory(),
            frame, CancellationToken.None);

        // prob = run the same ONNX file through the dispatcher
        float[] tensorArray = (float[])tensor.Materialized!;
        InferenceDispatcher dispatcher = new(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        BundleManifest bundle = new(
            BundleId: $"ppocr_det",
            Sessions: new Dictionary<string, string>(StringComparer.Ordinal) { ["default"] = onnxPath },
            PreferredBackends: Array.Empty<InferenceBackendId>());
        var sessions = await dispatcher.LoadBundleAsync(
            bundle, new InferencePreferences(), CancellationToken.None);
        using IInferenceSession session = sessions["default"];

        TensorSpec inputSpec = session.Inputs[0];
        TensorSpec outputSpec = session.Outputs[0];
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        float[] probArray;
        try
        {
            int[] shape = [1, 3, rh.AsInt32(), rw.AsInt32()];
            inputBag.Add<float>(inputSpec.Name, DataKind.Float32, shape, tensorArray);
            outputBag = await session.RunAsync(inputBag, CancellationToken.None);
            outputBag.TryGet(outputSpec.Name, out IInferenceTensor probTensor);
            probArray = probTensor.AsSpan<float>().ToArray();
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }

        // dbnet_postprocess(prob, rh, rw, scale_x, scale_y, ...)
        float scaleX = (float)testImage.Width / rw.AsInt32();
        float scaleY = (float)testImage.Height / rh.AsInt32();
        DbnetPostprocessFunction postFn = new();
        ValueRef sqlOutput = await postFn.ExecuteAsync(
            new[]
            {
                ValueRef.FromPrimitiveArray(probArray, DataKind.Float32),
                rh, rw,
                ValueRef.FromFloat32(scaleX), ValueRef.FromFloat32(scaleY),
                ValueRef.FromFloat32(0.3f), ValueRef.FromFloat32(0.6f),
                ValueRef.FromInt32(3),
                ValueRef.FromFloat32(1.5f),
            }.AsMemory(),
            frame, CancellationToken.None);

        DetectionRecord[] sqlDetections = ExtractDetections(sqlOutput);

        // === Compare ===
        // Both pipelines apply the same algorithm to the same input. Counts
        // and boxes must align modulo numerical noise.
        Assert.InRange(sqlDetections.Length - referenceDetections.Length, -1, 1);

        int matched = 0;
        foreach (DetectionRecord s in sqlDetections)
        {
            float csx = s.X + s.W / 2;
            float csy = s.Y + s.H / 2;
            foreach (DetectionRecord r in referenceDetections)
            {
                float crx = r.X + r.W / 2;
                float cry = r.Y + r.H / 2;
                if (MathF.Abs(csx - crx) <= 2f && MathF.Abs(csy - cry) <= 2f
                    && MathF.Abs(s.W - r.W) <= 2f && MathF.Abs(s.H - r.H) <= 2f)
                {
                    matched++;
                    break;
                }
            }
        }
        int minMatches = System.Math.Max(0, sqlDetections.Length - 1);
        Assert.True(matched >= minMatches,
            $"Only {matched}/{sqlDetections.Length} SQL detections matched the C# reference within tolerance.");
    }

    private static DetectionRecord[] ExtractDetections(ValueRef arrayValue)
    {
        if (arrayValue.IsNull) return [];
        ReadOnlySpan<ValueRef> elements = arrayValue.GetArrayElements();
        DetectionRecord[] result = new DetectionRecord[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            ReadOnlySpan<ValueRef> fields = elements[i].GetStructFields();
            result[i] = new DetectionRecord(
                fields[0].AsString(),
                fields[1].AsFloat32(),
                fields[2].AsFloat32(),
                fields[3].AsFloat32(),
                fields[4].AsFloat32(),
                fields[5].AsFloat32());
        }
        return result;
    }

    private EvaluationFrame MakeFrame()
    {
        Arena arena = new();
        return new EvaluationFrame(Row.Empty, arena, arena, types: new TypeRegistry());
    }

    private static float[] ImageNetMean() => [0.485f, 0.456f, 0.406f];
    private static float[] ImageNetStd() => [0.229f, 0.224f, 0.225f];

    private sealed record DetectionRecord(
        string Label,
        float Score,
        float X,
        float Y,
        float W,
        float H);
}
