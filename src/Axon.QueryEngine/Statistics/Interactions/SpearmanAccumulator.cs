namespace Axon.QueryEngine.Statistics.Interactions;

using Axon.QueryEngine.Model;

/// <summary>
/// Computes Spearman rank correlation between two numeric columns.
/// Uses reservoir sampling (Algorithm R) to collect up to <see cref="MaxSamples"/> pairs,
/// then at result time ranks both columns and computes Pearson on the ranks.
/// Handles tied values by assigning the average of tied ranks.
/// </summary>
public sealed class SpearmanAccumulator
{
    /// <summary>Maximum number of sample pairs to retain.</summary>
    public const int MaxSamples = 10_000;

    private readonly List<(float X, float Y)> _samples = new();
    private long _totalCount;
    private readonly Random _random = new(42);

    /// <summary>
    /// Adds a pair of numeric values. Both must be Scalar or UInt8.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        if (valueA.IsNull || valueB.IsNull)
        {
            return;
        }

        float x = ToFloat(valueA);
        float y = ToFloat(valueB);

        if (float.IsNaN(x) || float.IsNaN(y))
        {
            return;
        }

        _totalCount++;

        if (_samples.Count < MaxSamples)
        {
            _samples.Add((x, y));
        }
        else
        {
            long j = _random.NextInt64(_totalCount);

            if (j < MaxSamples)
            {
                _samples[(int)j] = (x, y);
            }
        }
    }

    /// <summary>
    /// Returns Spearman's rank correlation ρ ∈ [-1, 1], or NaN if insufficient data.
    /// </summary>
    public double GetValue()
    {
        if (_samples.Count < 2)
        {
            return double.NaN;
        }

        int n = _samples.Count;
        float[] xValues = new float[n];
        float[] yValues = new float[n];

        for (int i = 0; i < n; i++)
        {
            xValues[i] = _samples[i].X;
            yValues[i] = _samples[i].Y;
        }

        double[] rankX = ComputeRanks(xValues);
        double[] rankY = ComputeRanks(yValues);

        return PearsonOnRanks(rankX, rankY);
    }

    internal static double[] ComputeRanks(float[] values)
    {
        int n = values.Length;
        int[] indices = new int[n];

        for (int i = 0; i < n; i++)
        {
            indices[i] = i;
        }

        float[] sorted = (float[])values.Clone();
        Array.Sort(sorted, indices);

        double[] ranks = new double[n];
        int position = 0;

        while (position < n)
        {
            int tieStart = position;

            while (position < n - 1 && sorted[position + 1] == sorted[tieStart])
            {
                position++;
            }

            // Average rank for this tie group (1-based)
            double averageRank = (tieStart + position) / 2.0 + 1.0;

            for (int k = tieStart; k <= position; k++)
            {
                ranks[indices[k]] = averageRank;
            }

            position++;
        }

        return ranks;
    }

    private static double PearsonOnRanks(double[] rankX, double[] rankY)
    {
        int n = rankX.Length;
        double meanX = 0;
        double meanY = 0;

        for (int i = 0; i < n; i++)
        {
            meanX += rankX[i];
            meanY += rankY[i];
        }

        meanX /= n;
        meanY /= n;

        double covariance = 0;
        double varianceX = 0;
        double varianceY = 0;

        for (int i = 0; i < n; i++)
        {
            double dx = rankX[i] - meanX;
            double dy = rankY[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        double denominator = Math.Sqrt(varianceX * varianceY);

        return denominator < double.Epsilon ? double.NaN : covariance / denominator;
    }

    private static float ToFloat(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            _ => float.NaN
        };
    }
}
