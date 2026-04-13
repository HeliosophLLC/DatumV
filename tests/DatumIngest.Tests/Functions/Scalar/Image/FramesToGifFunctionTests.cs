using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Image;
using DatumIngest.Model;
using DatumIngest.Pooling;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Image;

/// <summary>
/// Phase D-B: <see cref="FramesToGifFunction"/> + <see cref="GifEncoder"/>.
/// Container framing (header / Netscape loop extension / trailer) is
/// asserted by calling the encoder directly; function-level behaviour
/// (null / empty / mismatched-dimension / fps validation) is asserted
/// through <c>ExecuteAsync</c>.
/// </summary>
public sealed class FramesToGifFunctionTests : ServiceTestBase
{
    /// <summary>
    /// Builds count distinct solid-coloured small frames. Each frame uses a
    /// different colour so the palette is forced to contain at least <paramref name="count"/>
    /// distinct opaque entries.
    /// </summary>
    private static SKBitmap[] MakeColouredBitmaps(int count, int size = 4)
    {
        SKBitmap[] frames = new SKBitmap[count];
        for (int i = 0; i < count; i++)
        {
            SKBitmap bmp = new(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(bmp))
            {
                canvas.Clear(new SKColor((byte)(i * 60), 100, 200, 255));
            }
            frames[i] = bmp;
        }
        return frames;
    }

    private static ValueRef[] WrapAsValueRefs(SKBitmap[] bitmaps)
    {
        ValueRef[] refs = new ValueRef[bitmaps.Length];
        for (int i = 0; i < bitmaps.Length; i++)
        {
            refs[i] = ValueRef.FromImage(bitmaps[i]);
        }
        return refs;
    }


    private async Task<ValueRef> ExecuteAsync(ValueRef framesArray, float fps)
    {
        var frame = CreateEvaluationFrame();
        return await new FramesToGifFunction().ExecuteAsync(
            new[] { framesArray, ValueRef.FromFloat32(fps) },
            frame, default);
    }

    // ----- Encoder byte-level checks (call GifEncoder.Encode directly) -----

    [Fact]
    public void Encoder_OutputStartsWithGif89aMagicBytes()
    {
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(3), delayCs: 8);

        Assert.True(bytes.Length >= 6);
        Assert.Equal((byte)'G', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'8', bytes[3]);
        Assert.Equal((byte)'9', bytes[4]);
        Assert.Equal((byte)'a', bytes[5]);
    }

    [Fact]
    public void Encoder_LogicalScreenDescriptor_StoresCanvasDimensions()
    {
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(2, size: 17), delayCs: 8);

        // Bytes 6,7 = canvas width LE; bytes 8,9 = canvas height LE.
        int w = bytes[6] | (bytes[7] << 8);
        int h = bytes[8] | (bytes[9] << 8);
        Assert.Equal(17, w);
        Assert.Equal(17, h);
    }

    [Fact]
    public void Encoder_OutputEndsWithGifTrailer()
    {
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(2), delayCs: 8);
        Assert.Equal(0x3B, bytes[^1]);
    }

    [Fact]
    public void Encoder_IncludesNetscapeLoopingExtension()
    {
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(2), delayCs: 8);

        ReadOnlySpan<byte> needle = "NETSCAPE2.0"u8;
        int idx = IndexOf(bytes, needle);
        Assert.True(idx >= 0, "expected Netscape looping extension in GIF output.");
    }

    [Fact]
    public void Encoder_FrameCount_MatchesInput()
    {
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(5), delayCs: 8);

        // Each frame contributes a Graphics Control Extension block prefixed by
        // 0x21 0xF9 0x04. Count occurrences — should equal the frame count.
        int found = 0;
        for (int i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0x21 && bytes[i + 1] == 0xF9 && bytes[i + 2] == 0x04)
            {
                found++;
            }
        }
        Assert.Equal(5, found);
    }

    [Fact]
    public void Encoder_SkiaCanDecodeFirstFrame()
    {
        // Skia decodes animated GIFs as the first frame only — that's fine for
        // a syntactic-validity check.
        byte[] bytes = GifEncoder.Encode(MakeColouredBitmaps(4, size: 8), delayCs: 10);

        SKBitmap? decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
        decoded.Dispose();
    }

    [Fact]
    public void Encoder_LargeFrame_PixelContentSurvivesDictGrowth()
    {
        // The encoded indices stream must run the LZW dictionary up past
        // multiple width boundaries (9→10→11→12 bits) to exercise the grow
        // logic. A 128×128 frame with content is enough — far past the
        // ~254-entry first-growth threshold. Verifies that what Skia
        // decodes back matches what we put in, beyond a few rows.
        SKBitmap bmp = new(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            // A filled circle that occupies the lower half of the canvas —
            // any decoder desync that truncates output will land here.
            using SKPaint paint = new() { Color = new SKColor(255, 200, 50), IsAntialias = false };
            canvas.DrawCircle(64, 96, 24, paint);
        }

        byte[] bytes = GifEncoder.Encode(new[] { bmp }, delayCs: 10);
        SKBitmap? decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(128, decoded.Width);
        Assert.Equal(128, decoded.Height);

        // A pixel near the centre of the circle (well below the first ~36
        // rows that the buggy encoder would manage) must come back as a
        // non-transparent, warmly-coloured pixel — close to (255, 200, 50)
        // after palette quantisation.
        SKColor pixel = decoded.GetPixel(64, 96);
        Assert.True(pixel.Alpha > 128,
            $"pixel at centre of lower-half circle should be opaque, got alpha={pixel.Alpha} "
            + $"(rgba={pixel.Red},{pixel.Green},{pixel.Blue},{pixel.Alpha}) — "
            + "decoder likely got truncated content from a desynced LZW stream.");
        Assert.True(pixel.Red > 200, $"pixel red component should be ≈255, got {pixel.Red}.");

        decoded.Dispose();
        bmp.Dispose();
    }

    [Fact]
    public void Encoder_MismatchedFrameDimensions_Throws()
    {
        SKBitmap a = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        SKBitmap b = new(new SKImageInfo(6, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            GifEncoder.Encode(new[] { a, b }, delayCs: 8));
        Assert.Contains("share dimensions", ex.Message);
        a.Dispose();
        b.Dispose();
    }

    // ----- Function-level behaviour -----

    [Fact]
    public async Task Function_ReturnsImageKind_NonNull()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Image, WrapAsValueRefs(MakeColouredBitmaps(3)));
        ValueRef result = await ExecuteAsync(arr, 12);

        Assert.Equal(DataKind.Image, result.Kind);
        Assert.False(result.IsNull);
        // Skia can re-decode the result back to a bitmap.
        SKBitmap bmp = result.AsImage();
        Assert.Equal(4, bmp.Width);
    }

    [Fact]
    public async Task Function_NullArray_ReturnsNullImage()
    {
        ValueRef nullArr = ValueRef.NullArray(DataKind.Image);
        ValueRef result = await ExecuteAsync(nullArr, 12);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Image, result.Kind);
    }

    [Fact]
    public async Task Function_EmptyArray_ReturnsNullImage()
    {
        ValueRef emptyArr = ValueRef.FromArray(DataKind.Image, Array.Empty<ValueRef>());
        ValueRef result = await ExecuteAsync(emptyArr, 12);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Function_NullFps_ReturnsNullImage()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Image, WrapAsValueRefs(MakeColouredBitmaps(2)));
        var frame = CreateEvaluationFrame();
        ValueRef result = await new FramesToGifFunction().ExecuteAsync(
            new[] { arr, ValueRef.Null(DataKind.Float32) },
            frame, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Function_NegativeFps_Throws()
    {
        ValueRef arr = ValueRef.FromArray(DataKind.Image, WrapAsValueRefs(MakeColouredBitmaps(2)));
        var frame = CreateEvaluationFrame();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new FramesToGifFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromFloat32(-5) },
                frame, default));
    }

    [Fact]
    public async Task Function_MismatchedFrameDimensions_ThrowsFunctionArgument()
    {
        SKBitmap a = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        SKBitmap b = new(new SKImageInfo(6, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas c1 = new(a)) c1.Clear(SKColors.Red);
        using (SKCanvas c2 = new(b)) c2.Clear(SKColors.Blue);
        ValueRef arr = ValueRef.FromArray(DataKind.Image,
            new[] { ValueRef.FromImage(a), ValueRef.FromImage(b) });

        var frame = CreateEvaluationFrame();
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await new FramesToGifFunction().ExecuteAsync(
                new[] { arr, ValueRef.FromFloat32(8) },
                frame, default));
        Assert.Contains("share dimensions", ex.Message);
    }

    [Fact]
    public async Task Function_NullFramesBecomeTransparentPlaceholders()
    {
        SKBitmap bmp = new(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (SKCanvas c = new(bmp)) c.Clear(SKColors.Red);
        ValueRef[] frames =
        [
            ValueRef.FromImage(bmp),
            ValueRef.Null(DataKind.Image),
            ValueRef.FromImage(bmp),
        ];
        ValueRef arr = ValueRef.FromArray(DataKind.Image, frames);
        ValueRef result = await ExecuteAsync(arr, 10);

        Assert.False(result.IsNull);
        SKBitmap decoded = result.AsImage();
        Assert.Equal(4, decoded.Width);
    }

    [Fact]
    public async Task Function_AllNullFrames_ReturnsNullImage()
    {
        ValueRef[] frames = [ValueRef.Null(DataKind.Image), ValueRef.Null(DataKind.Image)];
        ValueRef arr = ValueRef.FromArray(DataKind.Image, frames);
        ValueRef result = await ExecuteAsync(arr, 10);
        Assert.True(result.IsNull);
    }

    // ----- end-to-end SQL -----

    [Fact]
    public async Task EndToEnd_FramesToGifThroughSql()
    {
        // Wiring + signature resolution: animate_frames returns Array<Image>,
        // frames_to_gif consumes it and returns Image. The result should be a
        // single non-null Image row.
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["x"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT frames_to_gif("
            + "    animate_frames(0.5, 6, point2d(8, 8), "
            + "        (t) -> draw_rect(point2d(0, 0), point2d(8, 8), color(100, 150, 200))), "
            + "    6) AS gif "
            + "FROM t",
            catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["gif"];
        Assert.Equal(DataKind.Image, value.Kind);
        Assert.False(value.IsNull);
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
