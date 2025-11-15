using System.Text;
using SkiaSharp;

namespace DatumIngest.Shell;

/// <summary>
/// Decodes an image with SkiaSharp, quantizes it to a small palette using
/// median-cut, and emits the result as a Sixel escape sequence for terminals
/// that support the protocol (Windows Terminal v1.22+, mintty, foot, WezTerm
/// in Sixel mode, etc.). Conhost and the legacy macOS Terminal do not support
/// Sixel and will print the raw escape bytes verbatim.
/// </summary>
internal static class SixelEncoder
{
    /// <summary>Approximate cell width in device pixels for sizing decisions.</summary>
    private const int CellPixelWidth = 8;

    /// <summary>Approximate cell height in device pixels for sizing decisions.</summary>
    private const int CellPixelHeight = 16;

    /// <summary>
    /// Decodes <paramref name="imageBytes"/>, scales the result to fit within the
    /// given cell footprint, quantizes to <paramref name="paletteSize"/> colors,
    /// and returns a Sixel-encoded string. Throws on decode failure.
    /// </summary>
    public static string EncodeImage(
        ReadOnlySpan<byte> imageBytes,
        int maxCellsWide = 30,
        int maxCellsTall = 10,
        int paletteSize = 64)
    {
        using SKBitmap? decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("Failed to decode image bytes (unsupported format or corrupt data).");

        ComputeFitSize(
            decoded.Width, decoded.Height,
            maxCellsWide * CellPixelWidth, maxCellsTall * CellPixelHeight,
            out int targetW, out int targetH);

        using SKBitmap resized = decoded.Resize(
                new SKSizeI(targetW, targetH),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("Failed to resize bitmap.");

        (byte[] indices, SKColor[] palette) = Quantize(resized, paletteSize);
        return Encode(resized.Width, resized.Height, indices, palette);
    }

    private static void ComputeFitSize(int srcW, int srcH, int maxW, int maxH, out int w, out int h)
    {
        if (srcW <= maxW && srcH <= maxH)
        {
            w = srcW;
            h = srcH;
            return;
        }
        double ratio = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        w = Math.Max(1, (int)(srcW * ratio));
        h = Math.Max(1, (int)(srcH * ratio));
    }

    /// <summary>
    /// Median-cut color quantization. Returns a per-pixel palette index array
    /// (length = width * height) and the chosen palette (length ≤ paletteSize).
    /// </summary>
    private static (byte[] indices, SKColor[] palette) Quantize(SKBitmap bmp, int paletteSize)
    {
        int n = bmp.Width * bmp.Height;
        SKColor[] pixels = bmp.Pixels;

        // Each "box" tracks the indices of pixels assigned to it. We start with
        // one box covering all pixels and split the box with the widest channel
        // range until we have paletteSize boxes (or no box has > 1 pixel).
        List<int[]> boxes = new() { Enumerable.Range(0, n).ToArray() };

        while (boxes.Count < paletteSize)
        {
            // Find the box with the widest channel range — that's the one we split.
            int splitIdx = -1;
            int splitRange = 0;
            int splitChannel = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                int[] box = boxes[b];
                if (box.Length <= 1) continue;
                (int range, int channel) = WidestChannel(box, pixels);
                if (range > splitRange)
                {
                    splitRange = range;
                    splitIdx = b;
                    splitChannel = channel;
                }
            }
            if (splitIdx < 0) break;

            int[] toSplit = boxes[splitIdx];
            Array.Sort(toSplit, (a, b) => Channel(pixels[a], splitChannel) - Channel(pixels[b], splitChannel));
            int mid = toSplit.Length / 2;
            int[] left = new int[mid];
            int[] right = new int[toSplit.Length - mid];
            Array.Copy(toSplit, 0, left, 0, mid);
            Array.Copy(toSplit, mid, right, 0, right.Length);
            boxes[splitIdx] = left;
            boxes.Add(right);
        }

        // Each box's representative color = mean of its pixels.
        SKColor[] palette = new SKColor[boxes.Count];
        byte[] indices = new byte[n];
        for (int b = 0; b < boxes.Count; b++)
        {
            int[] box = boxes[b];
            long sumR = 0, sumG = 0, sumB = 0;
            foreach (int p in box)
            {
                SKColor c = pixels[p];
                sumR += c.Red;
                sumG += c.Green;
                sumB += c.Blue;
            }
            palette[b] = new SKColor((byte)(sumR / box.Length), (byte)(sumG / box.Length), (byte)(sumB / box.Length));
            byte idx = (byte)b;
            foreach (int p in box) indices[p] = idx;
        }

        return (indices, palette);
    }

    private static (int range, int channel) WidestChannel(int[] box, SKColor[] pixels)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        foreach (int p in box)
        {
            SKColor c = pixels[p];
            if (c.Red < rMin) rMin = c.Red;
            if (c.Red > rMax) rMax = c.Red;
            if (c.Green < gMin) gMin = c.Green;
            if (c.Green > gMax) gMax = c.Green;
            if (c.Blue < bMin) bMin = c.Blue;
            if (c.Blue > bMax) bMax = c.Blue;
        }
        int rRange = rMax - rMin;
        int gRange = gMax - gMin;
        int bRange = bMax - bMin;
        if (rRange >= gRange && rRange >= bRange) return (rRange, 0);
        if (gRange >= bRange) return (gRange, 1);
        return (bRange, 2);
    }

    private static int Channel(SKColor c, int channel) => channel switch
    {
        0 => c.Red,
        1 => c.Green,
        _ => c.Blue,
    };

    /// <summary>
    /// Emits a Sixel escape sequence given quantized pixel indices and a palette.
    /// </summary>
    private static string Encode(int width, int height, byte[] indices, SKColor[] palette)
    {
        StringBuilder sb = new(capacity: width * height / 2);

        // DCS introducer: P1=0 (no aspect), P2=1 (background = transparent), P3=0 (default grid). q ends params.
        sb.Append('').Append("P0;1;0q");

        // Raster attributes: aspect 1:1 over <width> x <height> pixels.
        sb.Append('"').Append("1;1;").Append(width).Append(';').Append(height);

        // Color definitions. Sixel uses HLS or RGB; we use RGB with each channel in 0..100.
        for (int i = 0; i < palette.Length; i++)
        {
            SKColor c = palette[i];
            int r = (c.Red * 100 + 127) / 255;
            int g = (c.Green * 100 + 127) / 255;
            int b = (c.Blue * 100 + 127) / 255;
            sb.Append('#').Append(i).Append(";2;").Append(r).Append(';').Append(g).Append(';').Append(b);
        }

        // Pixel data. Sixel encodes 6 vertical pixels per character byte (bit 0 = top, bit 5 = bottom).
        // For each band of 6 rows, for each color used in that band, walk the columns
        // and emit run-length-encoded sextets representing only that color's pixels.
        int bands = (height + 5) / 6;
        bool[] colorUsed = new bool[palette.Length];

        for (int band = 0; band < bands; band++)
        {
            int yStart = band * 6;
            int yCount = Math.Min(6, height - yStart);

            // Find which colors appear in this band.
            Array.Clear(colorUsed);
            for (int y = 0; y < yCount; y++)
            {
                int rowBase = (yStart + y) * width;
                for (int x = 0; x < width; x++)
                {
                    colorUsed[indices[rowBase + x]] = true;
                }
            }

            bool isFirstColor = true;
            for (int color = 0; color < palette.Length; color++)
            {
                if (!colorUsed[color]) continue;
                if (!isFirstColor) sb.Append('$');
                isFirstColor = false;

                sb.Append('#').Append(color);

                // Build sextets for this color, with RLE.
                char prev = '\0';
                int runLength = 0;
                for (int x = 0; x < width; x++)
                {
                    byte sixel = 0;
                    for (int y = 0; y < yCount; y++)
                    {
                        if (indices[(yStart + y) * width + x] == color)
                        {
                            sixel |= (byte)(1 << y);
                        }
                    }
                    char c = (char)(0x3F + sixel);
                    if (c == prev)
                    {
                        runLength++;
                    }
                    else
                    {
                        if (runLength > 0) AppendRun(sb, prev, runLength);
                        prev = c;
                        runLength = 1;
                    }
                }
                if (runLength > 0) AppendRun(sb, prev, runLength);
            }

            if (band < bands - 1) sb.Append('-');
        }

        // String terminator.
        sb.Append('').Append('\\');
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, char c, int count)
    {
        // RLE only saves bytes once the run length is ≥ 4 (the marker '!N' is at
        // least 3 chars by itself). Below that, emit the chars literally.
        if (count >= 4)
        {
            sb.Append('!').Append(count).Append(c);
        }
        else
        {
            for (int i = 0; i < count; i++) sb.Append(c);
        }
    }
}
