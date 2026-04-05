using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// DIY GIF89a animated-image encoder. Takes a sequence of equally-sized
/// RGBA <see cref="SKBitmap"/> frames + a per-frame delay and writes a
/// looping animated GIF byte stream.
/// </summary>
/// <remarks>
/// <para>
/// SkiaSharp can decode GIFs (first frame only) but has no GIF encoder, so
/// we build one ourselves. Three concerns: a 256-entry palette shared by
/// all frames (median-cut over the union of opaque pixels), per-frame LZW
/// compression of the indexed pixel data, and the GIF89a container framing
/// (logical screen descriptor, Netscape looping extension, per-frame
/// graphics control + image descriptor blocks, trailer).
/// </para>
/// <para>
/// Trade-offs for v1:
/// </para>
/// <list type="bullet">
///   <item>Single global colour table — no per-frame local tables. Cheap
///     and fine for procedurally-rendered drawings whose palette barely
///     changes across frames.</item>
///   <item>No dithering — nearest-neighbour quantisation. Adds banding on
///     smooth gradients; acceptable for shape-heavy content.</item>
///   <item>Each frame writes its full bounding box (no inter-frame diff).
///     Disposal method 2 ("restore to background") wipes transparent
///     areas between frames so they don't accumulate.</item>
///   <item>Index 0 reserved as transparent — pixels with alpha &lt; 128
///     map to it; everything else maps to the closest opaque entry.</item>
/// </list>
/// </remarks>
internal static class GifEncoder
{
    /// <summary>GIF mandates max code width of 12 bits.</summary>
    private const int MaxCodeBits = 12;

    /// <summary>Maximum LZW dictionary entry index before a CLEAR must be emitted.</summary>
    private const int MaxCode = (1 << MaxCodeBits) - 1;

    /// <summary>
    /// Encodes the supplied frames as an animated GIF89a byte stream.
    /// All frames must share the canvas dimensions; the first frame's
    /// width/height become the logical screen size.
    /// </summary>
    /// <param name="frames">Frame bitmaps in playback order. Must be non-empty.</param>
    /// <param name="delayCs">Per-frame delay in centiseconds (1 cs = 10 ms).</param>
    /// <returns>The complete animated GIF byte stream.</returns>
    public static byte[] Encode(IReadOnlyList<SKBitmap> frames, int delayCs)
    {
        if (frames is null || frames.Count == 0)
        {
            throw new ArgumentException("at least one frame is required.", nameof(frames));
        }
        SKBitmap first = frames[0];
        int width = first.Width;
        int height = first.Height;
        if (width <= 0 || width > ushort.MaxValue || height <= 0 || height > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"frame dimensions must fit in a uint16 ({width}×{height}).", nameof(frames));
        }
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].Width != width || frames[i].Height != height)
            {
                throw new ArgumentException(
                    $"all frames must share dimensions; frame[0] is {width}×{height} "
                    + $"but frame[{i}] is {frames[i].Width}×{frames[i].Height}.",
                    nameof(frames));
            }
        }

        // Build the shared 256-entry global colour table. Index 0 = transparent.
        byte[] palette = BuildSharedPalette(frames);

        using MemoryStream ms = new(capacity: 4096 + (width * height * frames.Count));
        WriteHeader(ms);
        WriteLogicalScreenDescriptor(ms, (ushort)width, (ushort)height);
        ms.Write(palette, 0, palette.Length);
        WriteNetscapeLoopingExtension(ms, loopCount: 0); // 0 = infinite

        // Quantisation cache: maps a 24-bit RGB key to its palette index. Drawings
        // tend to reuse colours heavily across pixels and frames, so caching pulls
        // the per-pixel lookup off the brute-force palette-scan path on the second
        // and later occurrences of a colour.
        Dictionary<uint, byte> quantizeCache = new(capacity: 1024);
        byte[] indices = new byte[width * height];

        for (int f = 0; f < frames.Count; f++)
        {
            QuantizeFrame(frames[f], palette, quantizeCache, indices);
            WriteGraphicsControlExtension(ms, delayCs);
            WriteImageDescriptor(ms, (ushort)width, (ushort)height);
            WriteLzwData(ms, indices, minCodeSize: 8);
        }

        ms.WriteByte(0x3B); // trailer
        return ms.ToArray();
    }

    // --- Header + framing ----------------------------------------------------

    private static void WriteHeader(MemoryStream ms)
    {
        // "GIF89a" — six ASCII bytes
        ms.WriteByte(0x47);
        ms.WriteByte(0x49);
        ms.WriteByte(0x46);
        ms.WriteByte(0x38);
        ms.WriteByte(0x39);
        ms.WriteByte(0x61);
    }

    private static void WriteLogicalScreenDescriptor(MemoryStream ms, ushort width, ushort height)
    {
        WriteUInt16Le(ms, width);
        WriteUInt16Le(ms, height);
        // Packed: GCT present (1) | colour resolution (7, so 3 bits = 0b111) | sort flag (0) | GCT size code (7 → 256 entries).
        // bit layout: 1_111_0_111 = 0xF7
        ms.WriteByte(0xF7);
        ms.WriteByte(0x00); // background colour index — 0 (our transparent slot)
        ms.WriteByte(0x00); // pixel aspect ratio — 0 (square)
    }

    private static void WriteNetscapeLoopingExtension(MemoryStream ms, ushort loopCount)
    {
        // Application extension block: introducer + label + block size + "NETSCAPE2.0" + sub-block + terminator.
        ms.WriteByte(0x21); // extension introducer
        ms.WriteByte(0xFF); // application label
        ms.WriteByte(0x0B); // block size (11 bytes follow)
        ReadOnlySpan<byte> netscape = "NETSCAPE2.0"u8;
        foreach (byte b in netscape)
        {
            ms.WriteByte(b);
        }
        ms.WriteByte(0x03); // sub-block size (3 bytes follow)
        ms.WriteByte(0x01); // sub-block id
        WriteUInt16Le(ms, loopCount); // 0 = infinite
        ms.WriteByte(0x00); // block terminator
    }

    private static void WriteGraphicsControlExtension(MemoryStream ms, int delayCs)
    {
        ms.WriteByte(0x21); // extension introducer
        ms.WriteByte(0xF9); // graphics control label
        ms.WriteByte(0x04); // block size

        // Packed: reserved(3) | disposal(3) | user_input(1) | transparent_color(1)
        // disposal = 2 (restore to background); transparent flag set so index 0 is honoured.
        // bit layout: 000_010_0_1 = 0x09
        ms.WriteByte(0x09);

        WriteUInt16Le(ms, (ushort)System.Math.Clamp(delayCs, 0, ushort.MaxValue));
        ms.WriteByte(0x00); // transparent colour index — matches our reserved slot 0
        ms.WriteByte(0x00); // block terminator
    }

    private static void WriteImageDescriptor(MemoryStream ms, ushort width, ushort height)
    {
        ms.WriteByte(0x2C); // image separator
        WriteUInt16Le(ms, 0); // left
        WriteUInt16Le(ms, 0); // top
        WriteUInt16Le(ms, width);
        WriteUInt16Le(ms, height);
        ms.WriteByte(0x00); // packed: no local colour table, no interlace, no sort, no LCT size
    }

    private static void WriteUInt16Le(MemoryStream ms, ushort value)
    {
        ms.WriteByte((byte)(value & 0xFF));
        ms.WriteByte((byte)((value >> 8) & 0xFF));
    }

    // --- Palette construction (median cut) -----------------------------------

    /// <summary>
    /// Returns a 768-byte RGB palette (256 entries × 3 bytes). Entry 0 is
    /// (0, 0, 0) reserved for transparent pixels; entries 1..255 are
    /// chosen by median-cut over the union of opaque pixels across all
    /// frames. If the content has fewer than 255 distinct opaque colours,
    /// unused entries also receive (0, 0, 0).
    /// </summary>
    private static byte[] BuildSharedPalette(IReadOnlyList<SKBitmap> frames)
    {
        // Histogram: 24-bit RGB key → occurrence count. We do not need the
        // pixels themselves — only the unique colours and their populations —
        // so a dictionary is enough.
        Dictionary<uint, int> histogram = new(capacity: 4096);
        foreach (SKBitmap frame in frames)
        {
            AccumulateOpaqueColours(frame, histogram);
        }

        byte[] palette = new byte[768];
        if (histogram.Count == 0)
        {
            return palette; // all transparent / empty — leave everything black
        }

        // Seed median-cut with one bucket holding every unique colour.
        List<ColourBucket> buckets = new()
        {
            ColourBucket.From(histogram),
        };

        const int targetBuckets = 255; // reserve slot 0 for transparent
        while (buckets.Count < targetBuckets)
        {
            // Pick the bucket with the largest range in any channel. If no
            // bucket has a positive range, there's nothing left to split.
            int splitIdx = -1;
            int largestRange = 0;
            for (int i = 0; i < buckets.Count; i++)
            {
                int r = buckets[i].LargestRange();
                if (r > largestRange)
                {
                    largestRange = r;
                    splitIdx = i;
                }
            }
            if (splitIdx < 0)
            {
                break;
            }

            (ColourBucket lhs, ColourBucket rhs) = buckets[splitIdx].Split();
            buckets[splitIdx] = lhs;
            buckets.Add(rhs);
        }

        // Emit each bucket's average colour into palette entries 1..N.
        for (int i = 0; i < buckets.Count && i < targetBuckets; i++)
        {
            (byte r, byte g, byte b) = buckets[i].AverageColour();
            int dst = (i + 1) * 3;
            palette[dst] = r;
            palette[dst + 1] = g;
            palette[dst + 2] = b;
        }
        return palette;
    }

    private static void AccumulateOpaqueColours(SKBitmap bitmap, Dictionary<uint, int> histogram)
    {
        SKBitmap rgba = bitmap;
        SKBitmap? converted = null;
        if (bitmap.ColorType != SKColorType.Rgba8888)
        {
            converted = new SKBitmap(new SKImageInfo(
                bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(converted))
            {
                canvas.DrawBitmap(bitmap, 0, 0);
            }
            rgba = converted;
        }
        try
        {
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a < 128)
                {
                    continue; // transparent pixels don't contribute to the palette
                }
                uint key = ((uint)pixels[i] << 16) | ((uint)pixels[i + 1] << 8) | pixels[i + 2];
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// One bucket inside the median-cut algorithm: a non-empty collection of
    /// (r, g, b, count) entries plus precomputed per-channel min/max ranges.
    /// </summary>
    private readonly struct ColourBucket
    {
        public readonly List<(byte R, byte G, byte B, int Count)> Pixels;
        public readonly byte MinR, MinG, MinB;
        public readonly byte MaxR, MaxG, MaxB;

        private ColourBucket(List<(byte R, byte G, byte B, int Count)> pixels)
        {
            Pixels = pixels;
            byte minR = 255, minG = 255, minB = 255;
            byte maxR = 0, maxG = 0, maxB = 0;
            foreach ((byte r, byte g, byte b, _) in pixels)
            {
                if (r < minR) minR = r;
                if (g < minG) minG = g;
                if (b < minB) minB = b;
                if (r > maxR) maxR = r;
                if (g > maxG) maxG = g;
                if (b > maxB) maxB = b;
            }
            MinR = minR; MinG = minG; MinB = minB;
            MaxR = maxR; MaxG = maxG; MaxB = maxB;
        }

        public static ColourBucket From(Dictionary<uint, int> histogram)
        {
            List<(byte, byte, byte, int)> pixels = new(histogram.Count);
            foreach (KeyValuePair<uint, int> kv in histogram)
            {
                byte r = (byte)((kv.Key >> 16) & 0xFF);
                byte g = (byte)((kv.Key >> 8) & 0xFF);
                byte b = (byte)(kv.Key & 0xFF);
                pixels.Add((r, g, b, kv.Value));
            }
            return new ColourBucket(pixels);
        }

        public int LargestRange()
        {
            int dr = MaxR - MinR;
            int dg = MaxG - MinG;
            int db = MaxB - MinB;
            int max = dr;
            if (dg > max) max = dg;
            if (db > max) max = db;
            return max;
        }

        /// <summary>
        /// Splits this bucket into two along the channel with the largest range,
        /// at the population median. Returns the (lower, upper) halves.
        /// </summary>
        public (ColourBucket Lhs, ColourBucket Rhs) Split()
        {
            int dr = MaxR - MinR;
            int dg = MaxG - MinG;
            int db = MaxB - MinB;
            int axis = 0; // 0=R, 1=G, 2=B
            int max = dr;
            if (dg > max) { axis = 1; max = dg; }
            if (db > max) { axis = 2; }

            List<(byte R, byte G, byte B, int Count)> sorted = new(Pixels);
            sorted.Sort(axis switch
            {
                0 => (a, b) => a.R.CompareTo(b.R),
                1 => (a, b) => a.G.CompareTo(b.G),
                _ => (a, b) => a.B.CompareTo(b.B),
            });

            // Walk by weight, finding the index that puts roughly half the
            // pixel-population on each side. Pure index-median ignores how
            // often each unique colour occurs; weighted-median splits in a
            // way that respects the visual prevalence of each colour.
            long total = 0;
            foreach (var p in sorted) total += p.Count;
            long half = total / 2;
            long running = 0;
            int splitAt = 1;
            for (int i = 0; i < sorted.Count; i++)
            {
                running += sorted[i].Count;
                if (running >= half)
                {
                    splitAt = i + 1;
                    break;
                }
            }
            if (splitAt < 1) splitAt = 1;
            if (splitAt >= sorted.Count) splitAt = sorted.Count - 1;
            if (splitAt < 1) splitAt = 1; // can't happen unless we had 1 element — guarded by LargestRange > 0

            List<(byte, byte, byte, int)> left = sorted.GetRange(0, splitAt);
            List<(byte, byte, byte, int)> right = sorted.GetRange(splitAt, sorted.Count - splitAt);
            return (new ColourBucket(left), new ColourBucket(right));
        }

        public (byte R, byte G, byte B) AverageColour()
        {
            long sumR = 0, sumG = 0, sumB = 0;
            long totalWeight = 0;
            foreach ((byte r, byte g, byte b, int c) in Pixels)
            {
                sumR += r * (long)c;
                sumG += g * (long)c;
                sumB += b * (long)c;
                totalWeight += c;
            }
            if (totalWeight == 0)
            {
                return (0, 0, 0);
            }
            return (
                (byte)(sumR / totalWeight),
                (byte)(sumG / totalWeight),
                (byte)(sumB / totalWeight));
        }
    }

    // --- Per-frame quantisation ---------------------------------------------

    /// <summary>
    /// Maps each pixel of <paramref name="frame"/> to a palette index in
    /// <paramref name="indices"/>. Transparent pixels (alpha &lt; 128) map
    /// to index 0; opaque pixels map to the closest palette entry by
    /// squared RGB distance. The cache memoises colour→index lookups
    /// across pixels and frames.
    /// </summary>
    private static void QuantizeFrame(
        SKBitmap frame,
        byte[] palette,
        Dictionary<uint, byte> cache,
        byte[] indices)
    {
        SKBitmap rgba = frame;
        SKBitmap? converted = null;
        if (frame.ColorType != SKColorType.Rgba8888)
        {
            converted = new SKBitmap(new SKImageInfo(
                frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new(converted))
            {
                canvas.DrawBitmap(frame, 0, 0);
            }
            rgba = converted;
        }
        try
        {
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            int dst = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a < 128)
                {
                    indices[dst++] = 0; // transparent
                    continue;
                }
                byte r = pixels[i];
                byte g = pixels[i + 1];
                byte b = pixels[i + 2];
                uint key = ((uint)r << 16) | ((uint)g << 8) | b;
                if (!cache.TryGetValue(key, out byte idx))
                {
                    idx = FindClosestPaletteEntry(r, g, b, palette);
                    cache[key] = idx;
                }
                indices[dst++] = idx;
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// Brute-force nearest-neighbour search over the 255 opaque palette
    /// entries (indices 1..255). Index 0 is reserved for transparent and
    /// never selected for an opaque pixel. Called once per unique colour
    /// thanks to the cache in <see cref="QuantizeFrame"/>.
    /// </summary>
    private static byte FindClosestPaletteEntry(byte r, byte g, byte b, byte[] palette)
    {
        int bestIdx = 1;
        int bestDist = int.MaxValue;
        for (int i = 1; i < 256; i++)
        {
            int pr = palette[i * 3];
            int pg = palette[i * 3 + 1];
            int pb = palette[i * 3 + 2];
            int dr = pr - r;
            int dg = pg - g;
            int db = pb - b;
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        return (byte)bestIdx;
    }

    // --- LZW encoder + sub-block packer -------------------------------------

    /// <summary>
    /// LZW-encodes <paramref name="indices"/> with the GIF variant of the
    /// algorithm: variable-width codes 9..12 bits, clear / EOI special
    /// codes, dictionary reset when full. Output is wrapped in 255-byte
    /// data sub-blocks terminated by a zero-length block.
    /// </summary>
    private static void WriteLzwData(MemoryStream ms, byte[] indices, int minCodeSize)
    {
        ms.WriteByte((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int nextCode = eoiCode + 1;
        int codeSize = minCodeSize + 1;

        // Dictionary as flat parent / suffix arrays: dict[parent, suffix] → code.
        // Using a Dictionary<long, int> keyed by (parent << 8 | suffix) keeps the
        // implementation compact and the working-set tight.
        Dictionary<int, int> dict = new(capacity: 4096);

        SubBlockBuffer buffer = new();
        BitPacker packer = new(buffer);

        packer.WriteBits(clearCode, codeSize);

        if (indices.Length == 0)
        {
            packer.WriteBits(eoiCode, codeSize);
            packer.Flush();
            buffer.FinishAndWriteTo(ms);
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int k = indices[i];
            int key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out int existing))
            {
                prefix = existing;
                continue;
            }

            packer.WriteBits(prefix, codeSize);

            if (nextCode <= MaxCode)
            {
                dict[key] = nextCode;
                nextCode++;
                // Grow code width to stay in sync with the standard GIF decoder
                // (libgif's `++RunningCode > (1 << RunningBits)`): the decoder
                // grows AFTER inserting the entry numerically equal to
                // `1 << bits`, so the just-inserted code's value crosses the
                // boundary. Using `==` here grows one entry early, which
                // desyncs the moment the first dictionary growth fires (~iter
                // 255 at minCodeSize=8) and corrupts every frame past that
                // point. Visible as "only the top N rows render, the rest
                // disappears" for any frame that runs the dictionary up.
                if (nextCode > (1 << codeSize) && codeSize < MaxCodeBits)
                {
                    codeSize++;
                }
            }
            else
            {
                // Dictionary is full — reset and start over.
                packer.WriteBits(clearCode, codeSize);
                dict.Clear();
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
            }
            prefix = k;
        }

        packer.WriteBits(prefix, codeSize);
        packer.WriteBits(eoiCode, codeSize);
        packer.Flush();
        buffer.FinishAndWriteTo(ms);
    }

    /// <summary>
    /// Packs successive variable-width codes into a byte stream, LSB-first
    /// within each byte (LZW-as-emitted convention). Pushed bytes go into
    /// a <see cref="SubBlockBuffer"/> which fragments them into 255-byte
    /// chunks at the GIF sub-block boundary.
    /// </summary>
    private sealed class BitPacker
    {
        private readonly SubBlockBuffer _sink;
        private uint _accumulator;
        private int _bitsInAccumulator;

        public BitPacker(SubBlockBuffer sink) { _sink = sink; }

        public void WriteBits(int value, int bits)
        {
            _accumulator |= (uint)(value & ((1 << bits) - 1)) << _bitsInAccumulator;
            _bitsInAccumulator += bits;
            while (_bitsInAccumulator >= 8)
            {
                _sink.WriteByte((byte)(_accumulator & 0xFF));
                _accumulator >>= 8;
                _bitsInAccumulator -= 8;
            }
        }

        public void Flush()
        {
            if (_bitsInAccumulator > 0)
            {
                _sink.WriteByte((byte)(_accumulator & 0xFF));
                _accumulator = 0;
                _bitsInAccumulator = 0;
            }
        }
    }

    /// <summary>
    /// Accumulates packed LZW bytes into 255-byte data sub-blocks and writes
    /// each block prefixed by its length. <see cref="FinishAndWriteTo"/>
    /// flushes the partial trailing block plus the zero-length terminator.
    /// </summary>
    private sealed class SubBlockBuffer
    {
        private readonly List<byte[]> _blocks = new();
        private byte[] _current = new byte[255];
        private int _currentLen;

        public void WriteByte(byte b)
        {
            _current[_currentLen++] = b;
            if (_currentLen == 255)
            {
                _blocks.Add(_current);
                _current = new byte[255];
                _currentLen = 0;
            }
        }

        public void FinishAndWriteTo(MemoryStream ms)
        {
            foreach (byte[] block in _blocks)
            {
                ms.WriteByte(255);
                ms.Write(block, 0, 255);
            }
            if (_currentLen > 0)
            {
                ms.WriteByte((byte)_currentLen);
                ms.Write(_current, 0, _currentLen);
            }
            ms.WriteByte(0x00); // block terminator
        }
    }
}
