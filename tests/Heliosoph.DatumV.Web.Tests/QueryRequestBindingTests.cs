using System.Text;
using System.Text.Json;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Heliosoph.DatumV.Web.Tests;

/// <summary>
/// Direct tests of <see cref="QueryRequestBinding"/> via in-memory
/// <see cref="DefaultHttpContext"/> instances. Covers both transports
/// (<c>application/json</c> and <c>multipart/form-data</c>) plus the
/// expected error shapes — unknown kind, ref without multipart, missing
/// part, ref against a non-binary kind.
/// </summary>
public sealed class QueryRequestBindingTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Json_NoParameters_ReturnsEmptyDict()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""{"sql":"SELECT 1"}""");

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);

        Assert.Equal("SELECT 1", envelope.Body.Sql);
        Assert.Empty(envelope.Parameters);
    }

    [Fact]
    public async Task Json_InlineInt32Parameter_BindsInlineParameter()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""
            {
              "sql": "SELECT $n + 1",
              "parameters": { "n": { "kind": "Int32", "value": 41 } }
            }
            """);

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);

        ParameterValue bound = envelope.Parameters["n"];
        InlineParameter inline = Assert.IsType<InlineParameter>(bound);
        Assert.Equal(41, inline.Value.AsInt32());
    }

    [Fact]
    public async Task Json_StringParameter_RoutesToStringParameter()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""
            {
              "sql": "SELECT $s",
              "parameters": { "s": { "kind": "String", "value": "hello" } }
            }
            """);

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);

        StringParameter str = Assert.IsType<StringParameter>(envelope.Parameters["s"]);
        Assert.Equal("hello", str.Value);
    }

    [Fact]
    public async Task Json_RefParameter_RejectedBecauseNoMultipart()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""
            {
              "sql": "SELECT 1",
              "parameters": { "img": { "kind": "Image", "ref": "img_part" } }
            }
            """);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => QueryRequestBinding.ReadAsync(ctx.Request, Json, default));
        Assert.Contains("img", ex.Message);
        Assert.Contains("multipart", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Json_UnknownKind_Errors()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""
            {
              "sql": "SELECT 1",
              "parameters": { "x": { "kind": "NotAKind", "value": 1 } }
            }
            """);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => QueryRequestBinding.ReadAsync(ctx.Request, Json, default));
        Assert.Contains("NotAKind", ex.Message);
    }

    [Fact]
    public async Task Multipart_BinaryRef_BindsBinaryParameter()
    {
        byte[] imageBytes = [0xFF, 0xD8, 0xFF, 0xE0]; // first bytes of a JPEG marker
        DefaultHttpContext ctx = MakeMultipartRequest(
            requestJson: """
                {
                  "sql": "SELECT length_bytes($img)",
                  "parameters": { "img": { "kind": "Image", "ref": "img_part" } }
                }
                """,
            ("img_part", "image/jpeg", imageBytes));

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);

        BinaryParameter bin = Assert.IsType<BinaryParameter>(envelope.Parameters["img"]);
        Assert.Equal(DataKind.Image, bin.Kind);
        Assert.Equal(imageBytes, bin.Bytes);
    }

    [Fact]
    public async Task Multipart_BinaryRefAndInlineScalar_BindsBoth()
    {
        byte[] imageBytes = [0x01, 0x02, 0x03];
        DefaultHttpContext ctx = MakeMultipartRequest(
            requestJson: """
                {
                  "sql": "SELECT image_resize($img, $w)",
                  "parameters": {
                    "img": { "kind": "Image", "ref": "img_part" },
                    "w":   { "kind": "Int32", "value": 320 }
                  }
                }
                """,
            ("img_part", "image/jpeg", imageBytes));

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);

        BinaryParameter bin = Assert.IsType<BinaryParameter>(envelope.Parameters["img"]);
        Assert.Equal(imageBytes, bin.Bytes);
        InlineParameter inline = Assert.IsType<InlineParameter>(envelope.Parameters["w"]);
        Assert.Equal(320, inline.Value.AsInt32());
    }

    [Fact]
    public async Task Multipart_MissingReferencedPart_Errors()
    {
        DefaultHttpContext ctx = MakeMultipartRequest(
            requestJson: """
                {
                  "sql": "SELECT 1",
                  "parameters": { "img": { "kind": "Image", "ref": "missing_part" } }
                }
                """);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => QueryRequestBinding.ReadAsync(ctx.Request, Json, default));
        Assert.Contains("missing_part", ex.Message);
    }

    [Fact]
    public async Task Multipart_RefAgainstNonBinaryKind_Errors()
    {
        byte[] junk = [0xAA, 0xBB];
        DefaultHttpContext ctx = MakeMultipartRequest(
            requestJson: """
                {
                  "sql": "SELECT 1",
                  "parameters": { "n": { "kind": "Int32", "ref": "part" } }
                }
                """,
            ("part", "application/octet-stream", junk));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => QueryRequestBinding.ReadAsync(ctx.Request, Json, default));
        Assert.Contains("not a binary kind", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multipart_MissingRequestPart_Errors()
    {
        DefaultHttpContext ctx = MakeMultipartRequest(
            requestJson: null,
            ("img_part", "image/jpeg", new byte[] { 1, 2 }));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => QueryRequestBinding.ReadAsync(ctx.Request, Json, default));
        Assert.Contains("request", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multipart_NullValueOnInline_BindsNullDataValue()
    {
        DefaultHttpContext ctx = MakeJsonRequest("""
            {
              "sql": "SELECT $x",
              "parameters": { "x": { "kind": "Int32", "value": null } }
            }
            """);

        QueryStreamEnvelope envelope = await QueryRequestBinding.ReadAsync(ctx.Request, Json, default);
        InlineParameter inline = Assert.IsType<InlineParameter>(envelope.Parameters["x"]);
        Assert.True(inline.Value.IsNull);
    }

    // ───────────────────── helpers ─────────────────────

    private static DefaultHttpContext MakeJsonRequest(string body)
    {
        DefaultHttpContext ctx = new();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.ContentLength = bytes.Length;
        // MultipartReader probes for IHttpRequestBodyDetectionFeature even on
        // non-multipart paths; the default context already provides one but
        // we add the form feature anyway in case downstream code touches it.
        ctx.Features.Set<IFormFeature>(new FormFeature(ctx.Request));
        return ctx;
    }

    private static DefaultHttpContext MakeMultipartRequest(
        string? requestJson,
        params (string Name, string ContentType, byte[] Bytes)[] binaryParts)
    {
        const string boundary = "----TestBoundary12345";
        MemoryStream body = new();

        if (requestJson is not null)
        {
            WritePart(body, $"--{boundary}\r\n");
            WritePart(body, "Content-Disposition: form-data; name=\"request\"\r\n");
            WritePart(body, "Content-Type: application/json\r\n\r\n");
            WritePart(body, requestJson);
            WritePart(body, "\r\n");
        }
        foreach ((string name, string contentType, byte[] bytes) in binaryParts)
        {
            WritePart(body, $"--{boundary}\r\n");
            WritePart(body, $"Content-Disposition: form-data; name=\"{name}\"; filename=\"{name}.bin\"\r\n");
            WritePart(body, $"Content-Type: {contentType}\r\n\r\n");
            body.Write(bytes, 0, bytes.Length);
            WritePart(body, "\r\n");
        }
        WritePart(body, $"--{boundary}--\r\n");

        body.Position = 0;

        DefaultHttpContext ctx = new();
        ctx.Request.Body = body;
        ctx.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        ctx.Request.ContentLength = body.Length;
        ctx.Features.Set<IFormFeature>(new FormFeature(ctx.Request));
        return ctx;
    }

    private static void WritePart(MemoryStream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
