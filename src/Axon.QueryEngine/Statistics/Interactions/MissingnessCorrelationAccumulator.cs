namespace Axon.QueryEngine.Statistics.Interactions;

using Axon.QueryEngine.Model;

/// <summary>
/// Computes Pearson correlation between the null masks of two columns using an online
/// algorithm (West 1979). Each value is mapped to 1.0 if null, 0.0 otherwise, producing
/// a correlation ∈ [-1, 1] that measures how strongly missingness in one column predicts
/// missingness in another. This is useful for detecting data leakage, pipeline bugs, or
/// structurally correlated missing data in ML datasets.
/// </summary>
public sealed class MissingnessCorrelationAccumulator
{
    private long _count;
    private double _meanX;
    private double _meanY;
    private double _m2X;
    private double _m2Y;
    private double _coMoment;

    /// <summary>
    /// Adds a pair of values. Both null and non-null values are meaningful inputs:
    /// null maps to 1.0, non-null maps to 0.0. Values are never skipped.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        double x = valueA.IsNull ? 1.0 : 0.0;
        double y = valueB.IsNull ? 1.0 : 0.0;

        _count++;

        double dx = x - _meanX;
        _meanX += dx / _count;
        double dy = y - _meanY;
        _meanY += dy / _count;

        _coMoment += dx * (y - _meanY);
        _m2X += dx * (x - _meanX);
        _m2Y += dy * (y - _meanY);
    }

    /// <summary>
    /// Returns the Pearson correlation coefficient r ∈ [-1, 1] between the null masks,
    /// or NaN if fewer than two rows have been observed or either column has zero variance
    /// (all-null or all-present).
    /// </summary>
    public double GetValue()
    {
        if (_count < 2)
        {
            return double.NaN;
        }

        double denominator = Math.Sqrt(_m2X * _m2Y);

        return denominator < double.Epsilon ? double.NaN : _coMoment / denominator;
    }
}
