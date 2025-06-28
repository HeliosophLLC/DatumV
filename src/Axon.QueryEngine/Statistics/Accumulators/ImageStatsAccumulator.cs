namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

/// <summary>
/// Accumulates image dimension and channel statistics by parsing JPEG, PNG, and WebP headers.
/// Does not fully decode images — only reads header bytes to extract width, height, and channel count.
/// Also tracks file size statistics using Welford's algorithm.
/// </summary>
public sealed class ImageStatsAccumulator : IStatisticAccumulator
{
    private long _count;
    private int _minWidth = int.MaxValue;
    private int _maxWidth = int.MinValue;
    private int _minHeight = int.MaxValue;
    private int _maxHeight = int.MinValue;
    private readonly Dictionary<int, long> _channelCounts = new();
    private long _undecodableCount;

    // File size Welford accumulators
    private long _sizeCount;
    private double _sizeMin = double.PositiveInfinity;
    private double _sizeMax = double.NegativeInfinity;
    private double _sizeMean;
    private double _sizeM2;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        byte[]? imageBytes = value.Kind switch
        {
            DataKind.Image => value.AsImage(),
            DataKind.UInt8Array => value.AsUInt8Array(),
            _ => null
        };

        if (imageBytes is null || imageBytes.Length == 0)
        {
            return;
        }

        _count++;

        // Track file size
        double fileSize = imageBytes.Length;
        _sizeCount++;

        if (fileSize < _sizeMin)
        {
            _sizeMin = fileSize;
        }

        if (fileSize > _sizeMax)
        {
            _sizeMax = fileSize;
        }

        double delta = fileSize - _sizeMean;
        _sizeMean += delta / _sizeCount;
        double delta2 = fileSize - _sizeMean;
        _sizeM2 += delta * delta2;

        // Parse image header for dimensions
        ImageDimensions? dimensions = TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            _undecodableCount++;
            return;
        }

        if (dimensions.Width < _minWidth)
        {
            _minWidth = dimensions.Width;
        }

        if (dimensions.Width > _maxWidth)
        {
            _maxWidth = dimensions.Width;
        }

        if (dimensions.Height < _minHeight)
        {
            _minHeight = dimensions.Height;
        }

        if (dimensions.Height > _maxHeight)
        {
            _maxHeight = dimensions.Height;
        }

        if (_channelCounts.TryGetValue(dimensions.Channels, out long count))
        {
            _channelCounts[dimensions.Channels] = count + 1;
        }
        else
        {
            _channelCounts[dimensions.Channels] = 1;
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not ImageStatsAccumulator otherImage || otherImage._count == 0)
        {
            return;
        }

        if (_count == 0)
        {
            _count = otherImage._count;
            _minWidth = otherImage._minWidth;
            _maxWidth = otherImage._maxWidth;
            _minHeight = otherImage._minHeight;
            _maxHeight = otherImage._maxHeight;
            _undecodableCount = otherImage._undecodableCount;
            _sizeCount = otherImage._sizeCount;
            _sizeMin = otherImage._sizeMin;
            _sizeMax = otherImage._sizeMax;
            _sizeMean = otherImage._sizeMean;
            _sizeM2 = otherImage._sizeM2;

            foreach (KeyValuePair<int, long> entry in otherImage._channelCounts)
            {
                _channelCounts[entry.Key] = entry.Value;
            }

            return;
        }

        _count += otherImage._count;
        _minWidth = Math.Min(_minWidth, otherImage._minWidth);
        _maxWidth = Math.Max(_maxWidth, otherImage._maxWidth);
        _minHeight = Math.Min(_minHeight, otherImage._minHeight);
        _maxHeight = Math.Max(_maxHeight, otherImage._maxHeight);
        _undecodableCount += otherImage._undecodableCount;

        // Merge channel counts
        foreach (KeyValuePair<int, long> entry in otherImage._channelCounts)
        {
            if (_channelCounts.TryGetValue(entry.Key, out long existing))
            {
                _channelCounts[entry.Key] = existing + entry.Value;
            }
            else
            {
                _channelCounts[entry.Key] = entry.Value;
            }
        }

        // Parallel Welford merge for file size
        if (otherImage._sizeCount > 0)
        {
            long combinedCount = _sizeCount + otherImage._sizeCount;
            double sizeDelta = otherImage._sizeMean - _sizeMean;
            _sizeMean += sizeDelta * otherImage._sizeCount / combinedCount;
            _sizeM2 += otherImage._sizeM2 +
                        sizeDelta * sizeDelta * _sizeCount * otherImage._sizeCount / combinedCount;
            _sizeMin = Math.Min(_sizeMin, otherImage._sizeMin);
            _sizeMax = Math.Max(_sizeMax, otherImage._sizeMax);
            _sizeCount = combinedCount;
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        double sizeVariance = _sizeCount > 1 ? _sizeM2 / _sizeCount : 0.0;
        long decodedCount = _count - _undecodableCount;

        return new StatisticResult("image_stats", new ImageStatsResult(
            _count,
            decodedCount > 0 ? _minWidth : 0,
            decodedCount > 0 ? _maxWidth : 0,
            decodedCount > 0 ? _minHeight : 0,
            decodedCount > 0 ? _maxHeight : 0,
            new Dictionary<int, long>(_channelCounts),
            _undecodableCount,
            new NumericSummary(
                _sizeCount,
                _sizeCount > 0 ? _sizeMin : double.NaN,
                _sizeCount > 0 ? _sizeMax : double.NaN,
                _sizeCount > 0 ? _sizeMean : double.NaN,
                sizeVariance,
                Math.Sqrt(sizeVariance))));
    }

    /// <summary>
    /// Attempts to parse image header bytes to extract dimensions without full decoding.
    /// Supports JPEG (SOF markers), PNG (IHDR chunk), and WebP (VP8/VP8L/VP8X).
    /// </summary>
    internal static ImageDimensions? TryParseHeader(byte[] data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        // JPEG: starts with FF D8
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryParseJpegHeader(data);
        }

        // PNG: starts with 89 50 4E 47 0D 0A 1A 0A
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return TryParsePngHeader(data);
        }

        // WebP: starts with RIFF....WEBP
        if (data.Length >= 16 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return TryParseWebPHeader(data);
        }

        return null;
    }

    private static ImageDimensions? TryParseJpegHeader(byte[] data)
    {
        // Scan for SOF markers: FF C0..C3, C5..C7, C9..CB, CD..CF
        int offset = 2; // skip SOI marker

        while (offset + 2 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = data[offset + 1];

            // Skip padding bytes (0xFF)
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            // SOF markers
            bool isSof = marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7)
                         or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

            if (isSof)
            {
                // Need at least 8 more bytes after the marker
                if (offset + 9 >= data.Length)
                {
                    return null;
                }

                // Skip marker (2) + length (2) + precision (1)
                int height = (data[offset + 5] << 8) | data[offset + 6];
                int width = (data[offset + 7] << 8) | data[offset + 8];
                int channels = data[offset + 9];

                return new ImageDimensions(width, height, channels);
            }

            // Skip to next marker: read segment length and advance
            if (offset + 3 >= data.Length)
            {
                return null;
            }

            int segmentLength = (data[offset + 2] << 8) | data[offset + 3];

            if (segmentLength < 2)
            {
                return null;
            }

            offset += 2 + segmentLength;
        }

        return null;
    }

    private static ImageDimensions? TryParsePngHeader(byte[] data)
    {
        // IHDR is always the first chunk after the 8-byte signature
        // Chunk: length(4) + type(4) + data + CRC(4)
        // IHDR data: width(4) + height(4) + bitDepth(1) + colorType(1) + ...
        if (data.Length < 24 + 2) // 8 sig + 4 len + 4 type + 4 width + 4 height + 1 depth + 1 color
        {
            return null;
        }

        // Verify IHDR type at offset 12
        if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
        {
            return null;
        }

        int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        byte colorType = data[25];

        int channels = colorType switch
        {
            0 => 1,  // Grayscale
            2 => 3,  // RGB
            3 => 1,  // Indexed (palette)
            4 => 2,  // Grayscale + Alpha
            6 => 4,  // RGBA
            _ => 0
        };

        return new ImageDimensions(width, height, channels);
    }

    private static ImageDimensions? TryParseWebPHeader(byte[] data)
    {
        if (data.Length < 20)
        {
            return null;
        }

        // Check chunk type at offset 12: VP8 (lossy), VP8L (lossless), VP8X (extended)
        bool isVp8 = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)' ';
        bool isVp8L = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'L';
        bool isVp8X = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'X';

        if (isVp8X && data.Length >= 30)
        {
            // VP8X: flags at byte 20, then 3 bytes reserved
            // Canvas width - 1 at bytes 24-26 (24-bit LE)
            // Canvas height - 1 at bytes 27-29 (24-bit LE)
            int width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            int height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            bool hasAlpha = (data[20] & 0x10) != 0;

            return new ImageDimensions(width, height, hasAlpha ? 4 : 3);
        }

        if (isVp8 && data.Length >= 30)
        {
            // VP8 lossy: skip chunk header (4 type + 4 size = offset 20)
            // Then 3-byte frame tag, then look for sync code 9D 01 2A
            int searchEnd = Math.Min(data.Length - 4, 40);

            for (int i = 20; i < searchEnd; i++)
            {
                if (data[i] == 0x9D && data[i + 1] == 0x01 && data[i + 2] == 0x2A)
                {
                    if (i + 6 >= data.Length)
                    {
                        return null;
                    }

                    int width = (data[i + 3] | (data[i + 4] << 8)) & 0x3FFF;
                    int height = (data[i + 5] | (data[i + 6] << 8)) & 0x3FFF;

                    return new ImageDimensions(width, height, 3); // VP8 lossy is always YUV → 3 channels
                }
            }
        }

        if (isVp8L && data.Length >= 25)
        {
            // VP8L lossless: offset 20 = chunk size (4 bytes), offset 24 = signature (0x2F)
            if (data[24] != 0x2F)
            {
                return null;
            }

            // Next 4 bytes: width-1 (14 bits) + height-1 (14 bits) + alpha (1 bit) + version (3 bits)
            if (data.Length < 29)
            {
                return null;
            }

            uint bits = (uint)(data[25] | (data[26] << 8) | (data[27] << 16) | (data[28] << 24));
            int width = (int)(bits & 0x3FFF) + 1;
            int height = (int)((bits >> 14) & 0x3FFF) + 1;
            bool hasAlpha = ((bits >> 28) & 1) == 1;

            return new ImageDimensions(width, height, hasAlpha ? 4 : 3);
        }

        return null;
    }
}

/// <summary>
/// Parsed image dimensions.
/// </summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Channels">Number of color channels (e.g. 1=grayscale, 3=RGB, 4=RGBA).</param>
public sealed record ImageDimensions(int Width, int Height, int Channels);

/// <summary>
/// Contains image statistics computed from header parsing.
/// </summary>
/// <param name="ImageCount">Number of image values observed.</param>
/// <param name="MinWidth">Minimum image width.</param>
/// <param name="MaxWidth">Maximum image width.</param>
/// <param name="MinHeight">Minimum image height.</param>
/// <param name="MaxHeight">Maximum image height.</param>
/// <param name="ChannelCounts">Distribution of channel counts (key=channels, value=count).</param>
/// <param name="UndecodableCount">Number of images whose headers could not be parsed.</param>
/// <param name="FileSizeStats">Aggregate file size statistics in bytes.</param>
public sealed record ImageStatsResult(
    long ImageCount,
    int MinWidth,
    int MaxWidth,
    int MinHeight,
    int MaxHeight,
    IReadOnlyDictionary<int, long> ChannelCounts,
    long UndecodableCount,
    NumericSummary FileSizeStats);
