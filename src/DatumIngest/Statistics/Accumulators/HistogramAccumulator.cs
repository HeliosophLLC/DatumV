namespace DatumQuery.Statistics.Accumulators;

using DatumQuery.Model;

/// <summary>
/// Builds an approximate histogram for numeric columns using reservoir sampling.
/// Collects up to <see cref="MaxSamples"/> values; once exceeded, uses Algorithm R
/// reservoir sampling. On <see cref="GetResult"/> the samples are binned into a fixed
/// number of equal-width bins between observed min and max.
/// </summary>
public sealed class HistogramAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    public const int MaxSamples = 100_000;

    /// <summary>Default number of histogram bins.</summary>
    public const int DefaultBinCount = 50;

    private readonly int _binCount;
    private readonly List<float> _samples = new();
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

        float numericValue = value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            _ => float.NaN
        };

        if (float.IsNaN(numericValue))
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
            return new StatisticResult("histogram", new HistogramResult([], []));
        }

        _samples.Sort();

        float min = _samples[0];
        float max = _samples[^1];

        if (Math.Abs(max - min) < float.Epsilon)
        {
            // All values are the same — single bin
            return new StatisticResult("histogram", new HistogramResult(
                [min, max],
                [_samples.Count]));
        }

        int effectiveBinCount = Math.Min(_binCount, _samples.Count);
        double binWidth = (double)(max - min) / effectiveBinCount;

        double[] binEdges = new double[effectiveBinCount + 1];

        for (int i = 0; i <= effectiveBinCount; i++)
        {
            binEdges[i] = min + i * binWidth;
        }

        // Ensure the last edge exactly equals max to avoid floating-point edge cases
        binEdges[^1] = max;

        long[] counts = new long[effectiveBinCount];

        foreach (float sample in _samples)
        {
            int bin = (int)((sample - min) / binWidth);

            if (bin >= effectiveBinCount)
            {
                bin = effectiveBinCount - 1;
            }

            counts[bin]++;
        }

        return new StatisticResult("histogram", new HistogramResult(binEdges, counts));
    }
}

/// <summary>
/// Contains histogram data with bin edges and counts.
/// </summary>
/// <param name="BinEdges">Edge values defining the bins (length = bin count + 1).</param>
/// <param name="Counts">Number of values in each bin (length = bin count).</param>
public sealed record HistogramResult(IReadOnlyList<double> BinEdges, IReadOnlyList<long> Counts)
{
    /// <summary>An empty histogram with no bins.</summary>
    public static HistogramResult Empty { get; } = new([], []);
}
