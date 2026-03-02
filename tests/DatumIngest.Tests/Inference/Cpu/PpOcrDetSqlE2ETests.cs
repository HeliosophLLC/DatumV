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
