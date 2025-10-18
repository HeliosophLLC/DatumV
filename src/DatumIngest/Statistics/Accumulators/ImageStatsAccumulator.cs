namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

/// <summary>
/// Accumulates image dimension and channel statistics by parsing JPEG, PNG, and WebP headers.
/// Does not fully decode images — only reads header bytes to extract width, height, and channel count.
/// Also tracks file size statistics using Welford's algorithm.
/// </summary>
public sealed class ImageStatsAccumulator : IStatisticAccumulator
{
    private const int MaxAspectSamples = 100_000;
    private const int AspectBinCount = 20;

    /// <summary>Width or height below this threshold flags an image as tiny.</summary>
    internal const int TinyThreshold = 32;

    /// <summary>Width or height above this threshold flags an image as huge.</summary>
    internal const int HugeThreshold = 4096;

    private long _count;
    private int _minWidth = int.MaxValue;
    private int _maxWidth = int.MinValue;
    private int _minHeight = int.MaxValue;
    private int _maxHeight = int.MinValue;
    private readonly Dictionary<int, long> _channelCounts = new();
    private readonly Dictionary<string, long> _orientationCounts = new();
    private long _undecodableCount;
    private long _tinyImageCount;
    private long _hugeImageCount;

    // File size Welford accumulators
    private long _sizeCount;
    private double _sizeMin = double.PositiveInfinity;
    private double _sizeMax = double.NegativeInfinity;
    private double _sizeMean;
    private double _sizeM2;

    // Megapixel Welford accumulators
    private long _megapixelCount;
    private double _megapixelMin = double.PositiveInfinity;
    private double _megapixelMax = double.NegativeInfinity;
    private double _megapixelMean;
    private double _megapixelM2;

    // Aspect ratio Welford accumulators
    private long _aspectCount;
    private double _aspectMin = double.PositiveInfinity;
    private double _aspectMax = double.NegativeInfinity;
    private double _aspectMean;
    private double _aspectM2;

    // Aspect ratio reservoir sampling
    private readonly List<float> _aspectSamples = new();
    private readonly Random _aspectRandom = new(42);
    private long _aspectTotalCount;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        byte[]? imageBytes = value.Kind switch
        {
            DataKind.Image => value.AsImage(store),
            DataKind.UInt8Array => value.AsUInt8Array(store),
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
        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            _undecodableCount++;
            return;
        }

        // Track extreme dimensions
        if (dimensions.Width < TinyThreshold || dimensions.Height < TinyThreshold)
        {
            _tinyImageCount++;
        }

        if (dimensions.Width > HugeThreshold || dimensions.Height > HugeThreshold)
        {
            _hugeImageCount++;
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

        // Track orientation
        string orientation = dimensions.Width > dimensions.Height ? "landscape"
                           : dimensions.Height > dimensions.Width ? "portrait"
                           : "square";

        if (_orientationCounts.TryGetValue(orientation, out long orientationCount))
        {
            _orientationCounts[orientation] = orientationCount + 1;
        }
        else
        {
            _orientationCounts[orientation] = 1;
        }

        // Track megapixel count
        double megapixels = (double)dimensions.Width * dimensions.Height / 1_000_000.0;
        _megapixelCount++;

        if (megapixels < _megapixelMin)
        {
            _megapixelMin = megapixels;
        }

        if (megapixels > _megapixelMax)
        {
            _megapixelMax = megapixels;
        }

        double megapixelDelta = megapixels - _megapixelMean;
        _megapixelMean += megapixelDelta / _megapixelCount;
        double megapixelDelta2 = megapixels - _megapixelMean;
        _megapixelM2 += megapixelDelta * megapixelDelta2;

        // Track aspect ratio
        if (dimensions.Height > 0)
        {
            float aspectRatio = (float)dimensions.Width / dimensions.Height;

            // Track aspect ratio summary statistics
            _aspectCount++;

            if (aspectRatio < _aspectMin)
            {
                _aspectMin = aspectRatio;
            }

            if (aspectRatio > _aspectMax)
            {
                _aspectMax = aspectRatio;
            }

            double aspectDelta = aspectRatio - _aspectMean;
            _aspectMean += aspectDelta / _aspectCount;
            double aspectDelta2 = aspectRatio - _aspectMean;
            _aspectM2 += aspectDelta * aspectDelta2;

            _aspectTotalCount++;

            if (_aspectSamples.Count < MaxAspectSamples)
            {
                _aspectSamples.Add(aspectRatio);
            }
            else
            {
                long j = _aspectRandom.NextInt64(_aspectTotalCount);

                if (j < MaxAspectSamples)
                {
                    _aspectSamples[(int)j] = aspectRatio;
                }
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        double sizeVariance = _sizeCount > 1 ? _sizeM2 / _sizeCount : 0.0;
        double megapixelVariance = _megapixelCount > 1 ? _megapixelM2 / _megapixelCount : 0.0;
        double aspectVariance = _aspectCount > 1 ? _aspectM2 / _aspectCount : 0.0;
        long decodedCount = _count - _undecodableCount;

        yield return new StatisticResult("image_stats", new ImageStatsResult(
            _count,
            decodedCount > 0 ? _minWidth : 0,
            decodedCount > 0 ? _maxWidth : 0,
            decodedCount > 0 ? _minHeight : 0,
            decodedCount > 0 ? _maxHeight : 0,
            new Dictionary<int, long>(_channelCounts),
            new Dictionary<string, long>(_orientationCounts),
            _undecodableCount,
            _tinyImageCount,
            _hugeImageCount,
            new NumericSummary(
                _sizeCount,
                _sizeCount > 0 ? _sizeMin : double.NaN,
                _sizeCount > 0 ? _sizeMax : double.NaN,
                _sizeCount > 0 ? _sizeMean : double.NaN,
                sizeVariance,
                Math.Sqrt(sizeVariance)),
            new NumericSummary(
                _megapixelCount,
                _megapixelCount > 0 ? _megapixelMin : double.NaN,
                _megapixelCount > 0 ? _megapixelMax : double.NaN,
                _megapixelCount > 0 ? _megapixelMean : double.NaN,
                megapixelVariance,
                Math.Sqrt(megapixelVariance)),
            new NumericSummary(
                _megapixelCount,
                _megapixelCount > 0 ? _megapixelMin * 1_000_000.0 : double.NaN,
                _megapixelCount > 0 ? _megapixelMax * 1_000_000.0 : double.NaN,
                _megapixelCount > 0 ? _megapixelMean * 1_000_000.0 : double.NaN,
                megapixelVariance * 1_000_000.0 * 1_000_000.0,
                Math.Sqrt(megapixelVariance) * 1_000_000.0),
            new NumericSummary(
                _aspectCount,
                _aspectCount > 0 ? _aspectMin : double.NaN,
                _aspectCount > 0 ? _aspectMax : double.NaN,
                _aspectCount > 0 ? _aspectMean : double.NaN,
                aspectVariance,
                Math.Sqrt(aspectVariance)),
            BuildAspectRatioHistogram()));
    }

    private HistogramResult? BuildAspectRatioHistogram()
    {
        if (_aspectSamples.Count == 0)
        {
            return null;
        }

        _aspectSamples.Sort();

        float min = _aspectSamples[0];
        float max = _aspectSamples[^1];

        if (Math.Abs(max - min) < float.Epsilon)
        {
            return new HistogramResult([min, max], [_aspectSamples.Count], false);
        }

        int effectiveBinCount = Math.Min(AspectBinCount, _aspectSamples.Count);
        double binWidth = (double)(max - min) / effectiveBinCount;

        double[] binEdges = new double[effectiveBinCount + 1];

        for (int i = 0; i <= effectiveBinCount; i++)
        {
            binEdges[i] = min + i * binWidth;
        }

        binEdges[^1] = max;

        long[] counts = new long[effectiveBinCount];

        foreach (float sample in _aspectSamples)
        {
            int bin = (int)((sample - min) / binWidth);

            if (bin >= effectiveBinCount)
            {
                bin = effectiveBinCount - 1;
            }

            counts[bin]++;
        }

        return new HistogramResult(binEdges, counts, false);
    }

}

/// <summary>
/// Contains image statistics computed from header parsing.
/// </summary>
/// <param name="ImageCount">Number of image values observed.</param>
/// <param name="MinWidth">Minimum image width.</param>
/// <param name="MaxWidth">Maximum image width.</param>
/// <param name="MinHeight">Minimum image height.</param>
/// <param name="MaxHeight">Maximum image height.</param>
/// <param name="ChannelCounts">Distribution of channel counts (key=channels, value=count).</param>
/// <param name="OrientationCounts">Distribution of image orientations (landscape, portrait, square).</param>
/// <param name="UndecodableCount">Number of images whose headers could not be parsed.</param>
/// <param name="TinyImageCount">Number of images with width &lt; 32 or height &lt; 32 pixels.</param>
/// <param name="HugeImageCount">Number of images with width &gt; 4096 or height &gt; 4096 pixels.</param>
/// <param name="FileSizeStats">Aggregate file size statistics in bytes.</param>
/// <param name="MegapixelStats">Summary statistics (min, max, mean, variance, stdDev) for megapixel counts (width × height / 1,000,000).</param>
/// <param name="PixelCountStats">Summary statistics (min, max, mean, variance, stdDev) for total pixel count (width × height).</param>
/// <param name="AspectRatioStats">Summary statistics (min, max, mean, variance, stdDev) for aspect ratios (width/height).</param>
/// <param name="AspectRatioHistogram">Optional histogram of aspect ratios.</param>
public sealed record ImageStatsResult(
    long ImageCount,
    int MinWidth,
    int MaxWidth,
    int MinHeight,
    int MaxHeight,
    IReadOnlyDictionary<int, long> ChannelCounts,
    IReadOnlyDictionary<string, long> OrientationCounts,
    long UndecodableCount,
    long TinyImageCount,
    long HugeImageCount,
    NumericSummary FileSizeStats,
    NumericSummary MegapixelStats,
    NumericSummary PixelCountStats,
    NumericSummary AspectRatioStats,
    HistogramResult? AspectRatioHistogram)
{
    /// <summary>An empty result with zero counts and NaN for all numeric summaries.</summary>
    public static ImageStatsResult Empty { get; } = new(
        0, 0, 0, 0, 0,
        new Dictionary<int, long>(), new Dictionary<string, long>(),
        0, 0, 0,
        NumericSummary.Empty, NumericSummary.Empty, NumericSummary.Empty, NumericSummary.Empty, null);
}
