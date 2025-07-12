namespace DatumIngest.Statistics.Interactions;

using DatumIngest.Model;

/// <summary>
/// Computes Pearson product-moment correlation coefficient between two numeric columns
/// using an online algorithm (West 1979). Tracks co-moment alongside individual means
/// and variances in a single pass with O(1) memory.
/// </summary>
public sealed class PearsonAccumulator
{
    private long _count;
    private double _meanX;
    private double _meanY;
    private double _m2X;
    private double _m2Y;
    private double _coMoment;

    /// <summary>
    /// Adds a pair of values. Both must be numeric (Scalar or UInt8).
    /// Null values and non-numeric kinds are skipped.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        if (valueA.IsNull || valueB.IsNull)
        {
            return;
        }

        double x = ToDouble(valueA);
        double y = ToDouble(valueB);

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return;
        }

        _count++;

        double dx = x - _meanX;
        _meanX += dx / _count;
        double dy = y - _meanY;
        _meanY += dy / _count;

        // Co-moment uses old dx with new mean_y
        _coMoment += dx * (y - _meanY);
        _m2X += dx * (x - _meanX);
        _m2Y += dy * (y - _meanY);
    }

    /// <summary>
    /// Returns the Pearson correlation coefficient r ∈ [-1, 1], or NaN if insufficient data
    /// or zero variance.
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

    private static double ToDouble(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            _ => double.NaN
        };
    }
}
