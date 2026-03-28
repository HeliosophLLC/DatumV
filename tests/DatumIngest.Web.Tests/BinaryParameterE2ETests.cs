using System.Text;
using System.Text.Json;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Web.Execution;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DatumIngest.Web.Tests;

/// <summary>
/// End-to-end coverage of the binary-parameter path through
/// <see cref="QueryStreamService"/>: a SQL script that references
/// <c>$img</c> as an <see cref="DataKind.Image"/> parameter, plus a
/// <see cref="BinaryParameter"/> carrying the encoded PNG bytes, should
/// produce a row event with the right value when an image function reads
/// it. Combined with <c>QueryRequestBindingTests.Multipart_BinaryRef_*</c>,
/// the full client → multipart → BinaryParameter → DataValue path is
/// covered without spinning up an HTTP server.
/// </summary>
public sealed class BinaryParameterE2ETests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly QueryStreamService _service;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public BinaryParameterE2ETests()
    {
        ServiceCollection services = new();
        services.AddDatumIngest();
        _services = services.BuildServiceProvider();
        Pool pool = _services.GetRequiredService<Pool>();
        TableCatalog catalog = new(pool);
        _service = new QueryStreamService(catalog);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task BinaryParameter_ImageWidth_ReturnsEncodedWidth()
    {
        const int width = 16;
        const int height = 9;
        byte[] pngBytes = EncodePng(width, height);

        Dictionary<string, ParameterValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["img"] = new BinaryParameter(DataKind.Image, pngBytes),
        };

        IReadOnlyList<JsonDocument> events = await RunAsync(
            "SELECT image_width($img)",
            parameters);

        JsonDocument? row = FindEvent(events, "row");
        Assert.NotNull(row);
        string rendered = row!.RootElement.GetProperty("cells").GetRawText();
        Assert.Contains(width.ToString(), rendered);
    }

    [Fact]
    public async Task BinaryParameter_ImageHeight_ReturnsEncodedHeight()
    {
        const int width = 16;
        const int height = 9;
        byte[] pngBytes = EncodePng(width, height);

        Dictionary<string, ParameterValue> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["img"] = new BinaryParameter(DataKind.Image, pngBytes),
        };

        IReadOnlyList<JsonDocument> events = await RunAsync(
            "SELECT image_height($img)",
            parameters);

        JsonDocument? row = FindEvent(events, "row");
        Assert.NotNull(row);
        string rendered = row!.RootElement.GetProperty("cells").GetRawText();
        Assert.Contains(height.ToString(), rendered);
    }

    // ───────────────────── helpers ─────────────────────

    private static byte[] EncodePng(int width, int height)
    {
        using SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(new SKColor(0xCC, 0x44, 0x88));
        using SKImage image = SKImage.FromBitmap(bmp);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private async Task<IReadOnlyList<JsonDocument>> RunAsync(
        string sql,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        using MemoryStream output = new();
        await _service.ExecuteAsync(
            sql,
            maxRows: 1000,
            trace: TraceOptions.Off,
            parameters,
            output,
            Json,
            CancellationToken.None);

        output.Position = 0;
        string text = Encoding.UTF8.GetString(output.ToArray());
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    private static JsonDocument? FindEvent(IReadOnlyList<JsonDocument> events, string type)
    {
        foreach (JsonDocument doc in events)
        {
            if (doc.RootElement.TryGetProperty("type", out JsonElement t)
                && t.GetString() == type)
            {
                return doc;
            }
        }
        return null;
    }
}
