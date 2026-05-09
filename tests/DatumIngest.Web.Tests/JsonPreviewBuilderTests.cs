using System.Text.Json;
using DatumIngest.Functions.Json;
using DatumIngest.Web.Execution;

namespace DatumIngest.Web.Tests;

/// <summary>
/// Covers <see cref="JsonPreviewBuilder"/> — bounded preview construction over
/// CBOR-encoded <c>DataKind.Json</c> values. The output text must always be valid
/// JSON of the truncated portion so the front-end's existing tree renderer keeps
/// working without changes.
/// </summary>
public sealed class JsonPreviewBuilderTests
{
    [Fact]
    public void SmallArray_DecodesInFull_NoPreviewEnvelope()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("""[1, 2, 3]""");

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        int[]? parsed = JsonSerializer.Deserialize<int[]>(text);
        Assert.NotNull(parsed);
        Assert.Equal([1, 2, 3], parsed!);
    }

    [Fact]
    public void SmallObject_DecodesInFull_NoPreviewEnvelope()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("""{"a":1,"b":2}""");

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        Dictionary<string, int>? parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(text);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!["a"]);
        Assert.Equal(2, parsed["b"]);
    }

    [Fact]
    public void Scalar_DecodesInFull()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("42");

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        Assert.Equal("42", text);
    }

    [Fact]
    public void StringScalar_DecodesInFull()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(""" "hello" """);

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        Assert.Equal("\"hello\"", text);
    }

    [Fact]
    public void LargeArray_TruncatesToElementCap_WithEnvelope()
    {
        // 100 elements, cap at 16.
        int[] source = Enumerable.Range(0, 100).ToArray();
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(JsonSerializer.Serialize(source));

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor, maxElements: 16);

        Assert.NotNull(preview);
        Assert.Equal(100, preview!.Total);
        Assert.Equal(16, preview.Shown);
        Assert.Equal("array", preview.Mode);

        int[]? parsed = JsonSerializer.Deserialize<int[]>(text);
        Assert.NotNull(parsed);
        Assert.Equal(16, parsed!.Length);
        Assert.Equal(Enumerable.Range(0, 16), parsed);
    }

    [Fact]
    public void LargeObject_TruncatesToFieldCap_WithEnvelope()
    {
        Dictionary<string, int> source = new();
        for (int i = 0; i < 30; i++) source[$"k{i:D2}"] = i;
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(JsonSerializer.Serialize(source));

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor, maxElements: 8);

        Assert.NotNull(preview);
        Assert.Equal(30, preview!.Total);
        Assert.Equal(8, preview.Shown);
        Assert.Equal("object", preview.Mode);

        Dictionary<string, int>? parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(text);
        Assert.NotNull(parsed);
        Assert.Equal(8, parsed!.Count);
    }

    [Fact]
    public void NestedArray_PreservedInPreview()
    {
        // Each element is itself a small object — the cap fires at the outer
        // array level, but every shown element keeps its full structure.
        string json = JsonSerializer.Serialize(
            Enumerable.Range(0, 50).Select(i => new { id = i, name = $"item{i}" }).ToArray());
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(json);

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor, maxElements: 5);

        Assert.NotNull(preview);
        Assert.Equal(50, preview!.Total);
        Assert.Equal(5, preview.Shown);

        // Round-trip — each shown element should still be a complete object.
        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(5, doc.RootElement.GetArrayLength());
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, el.ValueKind);
            Assert.True(el.TryGetProperty("id", out _));
            Assert.True(el.TryGetProperty("name", out _));
        }
    }

    [Fact]
    public void ByteCap_FiresBeforeElementCap_WhenElementsAreLarge()
    {
        // 50 elements, each a wide string. Element cap (1000) won't fire;
        // byte cap (200 bytes) will.
        string[] source = Enumerable.Range(0, 50)
            .Select(i => new string('x', 60))
            .ToArray();
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(JsonSerializer.Serialize(source));

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(
            cbor, maxElements: 1000, maxBytes: 200);

        Assert.NotNull(preview);
        Assert.Equal(50, preview!.Total);
        Assert.True(preview.Shown < 50);
        Assert.True(preview.Shown >= 1);
    }

    [Fact]
    public void EmptyArray_DecodesAsEmpty_NoEnvelope()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("[]");

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        Assert.Equal("[]", text);
    }

    [Fact]
    public void EmptyObject_DecodesAsEmpty_NoEnvelope()
    {
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("{}");

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.Null(preview);
        Assert.Equal("{}", text);
    }

    [Fact]
    public void ExactlyAtCap_NoTruncation()
    {
        int[] source = Enumerable.Range(0, 16).ToArray();
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(JsonSerializer.Serialize(source));

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor, maxElements: 16);

        Assert.Null(preview);
        int[]? parsed = JsonSerializer.Deserialize<int[]>(text);
        Assert.Equal(16, parsed!.Length);
    }

    [Fact]
    public void CocoShape_AnnotationsArrayTruncates_FullObjectsPreserved()
    {
        // Tiny COCO-shaped fixture — verifies the realistic path.
        var annotations = Enumerable.Range(1, 100).Select(i => new
        {
            id = i,
            image_id = i / 5,
            category_id = 1,
            caption = $"A photo of subject {i}",
        }).ToArray();
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(JsonSerializer.Serialize(annotations));

        (string text, JsonPreviewInfo? preview) = JsonPreviewBuilder.Build(cbor);

        Assert.NotNull(preview);
        Assert.Equal(100, preview!.Total);
        Assert.Equal(JsonPreviewBuilder.MaxElements, preview.Shown);
        Assert.Equal("array", preview.Mode);

        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement firstAnnotation = doc.RootElement[0];
        Assert.Equal(1, firstAnnotation.GetProperty("id").GetInt32());
        Assert.Equal("A photo of subject 1", firstAnnotation.GetProperty("caption").GetString());
    }
}
