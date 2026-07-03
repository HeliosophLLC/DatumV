using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models;

using Microsoft.Extensions.Logging.Abstractions;

using SkiaSharp;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// End-to-end regression for the SQL-defined TrOCR printed recognizer, and
/// specifically for the KV-cache decode loop behind <c>decode_seq2seq</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this test exists.</strong> TrOCR runs through the KV-cache
/// branch of <c>decode_seq2seq</c>. A bug there re-read the cross-attention
/// (encoder) cache from the merged decoder's <c>present.*.encoder.*</c>
/// outputs on every step — but those come back empty on the with-past branch,
/// so the cross cache was wiped after the first generated token. The decoder
/// then free-ran on its language prior: the first one or two tokens decoded
/// correctly, then the rest was garbage ("Ice Tea" → "ICE TEEMED AHRICE").
/// A single-token image would have masked it, so this test uses a multi-word
/// line and asserts the WHOLE line survives, not just its prefix.
/// </para>
/// <para>
/// <strong>Model file.</strong> Downloaded on demand via
/// <see cref="ServiceTestBase.EnsureModelDownloadedAsync"/>; soft-skips when
/// offline so CI without internet stays green.
/// </para>
/// </remarks>
[Trait("Category", "CpuInference")]
public sealed class TrOcrPrintedSqlE2ETests : ServiceTestBase
{
    private const string ModelId = "trocr-base-printed";
    private const string EncoderRelPath = "onnx/encoder_model.onnx";

    /// <summary>Best-effort model fetch; null on any failure (offline CI).</summary>
    private async Task<bool> TryEnsureModelAvailableAsync()
    {
        if (File.Exists(GetDownloadedModelPath(ModelId, EncoderRelPath)))
        {
            return true;
        }
        try
        {
            await EnsureModelDownloadedAsync(ModelId);
        }
        catch
        {
            return false;
        }
        return File.Exists(GetDownloadedModelPath(ModelId, EncoderRelPath));
    }

    /// <summary>Loads the catalog's canonical install SQL for the model.</summary>
    private string LoadCanonicalSql()
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogVariant model = store.Manifest.Entries
            .SelectMany(e => e.Variants).First(v => v.Id == ModelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{ModelId}' has no installSql; can't run the SQL E2E test.");
        }
        return File.ReadAllText(Path.Combine(store.ManifestDirectory, model.InstallSql));
    }

    /// <summary>
    /// Renders one clean line of black printed text on white — the tight
    /// single-line crop TrOCR expects. The canvas is line-shaped; TrOCR
    /// resizes to 384×384 internally (matching its training distribution).
    /// </summary>
    private static byte[] MakeLineImagePng(string text)
    {
        using SKFont font = new(SKTypeface.Default, 60);
        using SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
        float textWidth = font.MeasureText(text, paint);

        int width = (int)MathF.Ceiling(textWidth) + 48;
        int height = 100;
        using SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText(text, 24, 70, SKTextAlign.Left, font, paint);
        }
        using SKData encoded = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>
    /// TrOCR must transcribe the full multi-word line — not just its first
    /// couple of tokens. With the cross-attention-cache bug the tail
    /// degenerated into garbage, dragging the normalized similarity far below
    /// this threshold; a correct decode lands at ~1.0.
    /// </summary>
    [Fact]
    public async Task TrOcrPrinted_TranscribesFullLine_NotJustPrefix()
    {
        if (!await TryEnsureModelAvailableAsync()) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        const string Expected = "HELLO WORLD";
        catalog.Add(new Heliosoph.DatumV.Catalog.Providers.InMemoryTableProvider(
            CreatePool(),
            "data",
            ["img"],
            [DataKind.Image],
            [new object?[] { MakeLineImagePng(Expected) }]));

        StatementPlan plan = catalog.Plan("SELECT models.trocr_printed(img) AS text FROM data");

        string? recognized = null;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                Assert.False(cell.IsNull, "trocr_printed returned NULL");
                recognized = cell.AsString(batch.Arena);
            }
        }

        Assert.NotNull(recognized);

        double similarity = NormalizedSimilarity(recognized!, Expected);
        Assert.True(
            similarity >= 0.6,
            $"TrOCR transcription '{recognized}' is too far from expected '{Expected}' "
            + $"(normalized similarity {similarity:F2}). A prefix-only match here is the "
            + "signature of the cross-attention KV-cache regression.");
    }

    /// <summary>
    /// Normalized-edit-distance similarity in [0, 1] over the alphanumeric,
    /// upper-cased projection of both strings (so casing, spacing, and
    /// punctuation don't dominate). 1.0 = identical after normalization.
    /// </summary>
    private static double NormalizedSimilarity(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 1.0;
        int distance = Levenshtein(na, nb);
        int max = Math.Max(na.Length, nb.Length);
        return max == 0 ? 1.0 : 1.0 - (double)distance / max;

        static string Normalize(string s)
        {
            Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
            int n = 0;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);
            }
            return new string(buf[..n]);
        }
    }

    private static int Levenshtein(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
