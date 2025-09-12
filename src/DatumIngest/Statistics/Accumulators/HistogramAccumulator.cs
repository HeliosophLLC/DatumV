namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Builds an approximate histogram for numeric columns using reservoir sampling.
/// Collects up to <see cref="MaxSamples"/> values; once exceeded, uses Algorithm R
/// reservoir sampling. On <see cref="GetResult"/> the samples are binned using
/// integer-aligned edges when all values are integral (producing human-readable bins
/// like [17, 18), [18, 19)...) or equal-width bins for continuous data.
/// </summary>
public sealed class HistogramAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    public const int MaxSamples = 100_000;

    /// <summary>Default number of histogram bins.</summary>
    public const int DefaultBinCount = 50;

    private readonly int _binCount;
    private readonly List<double> _samples = new();
    private readonly Random _random = new(42); // deterministic seed for reproducibility
    private long _totalCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistogramAccumulator"/> class.
    /// </summary>
    /// <param name="binCount">Number of histogram bins. Defaults to 50.</param>
    public HistogramAccumulator(int binCount = DefaultBinCount)
    {
        _binCount = binCount;
    }

    /// <summary>Gets the total number of values observed (may exceed <see cref="MaxSamples"/>).</summary>
    public long TotalCount => _totalCount;

    /// <summary>Gets the current number of samples retained.</summary>
    public int SampleCount => _samples.Count;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        double numericValue = value.Kind switch
        {
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Duration => value.AsDuration().TotalSeconds,
            _ => double.NaN
        };

        if (double.IsNaN(numericValue))
        {
            return;
        }

        _totalCount++;

        if (_samples.Count < MaxSamples)
        {
            _samples.Add(numericValue);
        }
        else
        {
            // Algorithm R reservoir sampling
            long j = _random.NextInt64(_totalCount);

            if (j < MaxSamples)
            {
                _samples[(int)j] = numericValue;
            }
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not HistogramAccumulator otherHistogram || otherHistogram._totalCount == 0)
        {
            return;
        }

        // Pool all samples, then downsample to MaxSamples if needed
        _totalCount += otherHistogram._totalCount;
        _samples.AddRange(otherHistogram._samples);

        if (_samples.Count > MaxSamples)
        {
            // Shuffle and truncate for approximate fairness
            for (int i = _samples.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_samples[i], _samples[j]) = (_samples[j], _samples[i]);
            }

            _samples.RemoveRange(MaxSamples, _samples.Count - MaxSamples);
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_samples.Count == 0)
        {
            return new StatisticResult("histogram", new HistogramResult([], [], false));
        }

        _samples.Sort();

        double min = _samples[0];
        double max = _samples[^1];

        if (Math.Abs(max - min) < double.Epsilon)
        {
            // All values are the same — single bin
            bool singleValueInteger = min == Math.Floor(min);
            return new StatisticResult("histogram", new HistogramResult(
                [min, max],
                [_samples.Count],
                singleValueInteger));
        }

        bool isIntegerData = IsIntegerData(_samples);
        int effectiveBinCount = Math.Min(_binCount, _samples.Count);

        double[] binEdges;
        long[] counts;

        if (isIntegerData)
        {
            BuildIntegerAlignedBins(min, max, effectiveBinCount, out binEdges, out counts);
        }
        else
        {
            BuildEqualWidthBins(min, max, effectiveBinCount, out binEdges, out counts);
        }

        return new StatisticResult("histogram", new HistogramResult(binEdges, counts, isIntegerData));
    }

    /// <summary>
    /// Determines whether all samples in the reservoir are integers (no fractional part).
    /// </summary>
    private static bool IsIntegerData(List<double> samples)
    {
        foreach (double sample in samples)
        {
            if (sample != Math.Floor(sample))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds bins with integer-aligned edges. When the number of distinct integer values
    /// fits within the bin count, each integer gets its own bin. Otherwise, the bin width
    /// is rounded up to the nearest integer so edges land on whole numbers.
    /// </summary>
    private void BuildIntegerAlignedBins(
        double min, double max, int requestedBinCount,
        out double[] binEdges, out long[] counts)
    {
        long intMin = (long)min;
        long intMax = (long)max;
        long distinctRange = intMax - intMin + 1;

        int effectiveBinCount;
        long binWidth;

        if (distinctRange <= requestedBinCount)
        {
            // One bin per integer value — exact histogram.
            effectiveBinCount = (int)distinctRange;
            binWidth = 1;
        }
        else
        {
            // Round bin width up to the nearest integer so edges stay aligned.
            binWidth = (distinctRange + requestedBinCount - 1) / requestedBinCount;
            effectiveBinCount = (int)((distinctRange + binWidth - 1) / binWidth);
        }

        binEdges = new double[effectiveBinCount + 1];

        for (int i = 0; i <= effectiveBinCount; i++)
        {
            binEdges[i] = intMin + i * binWidth;
        }

        counts = new long[effectiveBinCount];

        foreach (double sample in _samples)
        {
            long offset = (long)sample - intMin;
            int bin = (int)(offset / binWidth);

            if (bin < 0)
            {
                bin = 0;
            }
            else if (bin >= effectiveBinCount)
            {
                bin = effectiveBinCount - 1;
            }

            counts[bin]++;
        }
    }

    /// <summary>
    /// Builds equal-width bins for continuous (non-integer) data.
    /// </summary>
    private void BuildEqualWidthBins(
        double min, double max, int effectiveBinCount,
        out double[] binEdges, out long[] counts)
    {
        double binWidth = (max - min) / effectiveBinCount;

        binEdges = new double[effectiveBinCount + 1];

        for (int i = 0; i <= effectiveBinCount; i++)
        {
            binEdges[i] = min + i * binWidth;
        }

        // Ensure the last edge exactly equals max to avoid floating-point edge cases
        binEdges[^1] = max;

        counts = new long[effectiveBinCount];

        foreach (double sample in _samples)
        {
            int bin = (int)((sample - min) / binWidth);

            if (bin >= effectiveBinCount)
            {
                bin = effectiveBinCount - 1;
            }

            counts[bin]++;
        }
    }
}

/// <summary>
/// Contains histogram data with bin edges and counts.
/// </summary>
/// <param name="BinEdges">Edge values defining the bins (length = bin count + 1).</param>
/// <param name="Counts">Number of values in each bin (length = bin count).</param>
/// <param name="IntegerValued">Whether all observed values were integers (no fractional part).</param>
public sealed record HistogramResult(IReadOnlyList<double> BinEdges, IReadOnlyList<long> Counts, bool IntegerValued)
{
    /// <summary>An empty histogram with no bins.</summary>
    public static HistogramResult Empty { get; } = new([], [], false);
}
