using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>CORR(y, x)</c>. Computes the Pearson correlation coefficient
/// between two numeric columns using an online co-moment algorithm (parallel
/// to Welford's algorithm for variance).
/// <para>
/// Returns a value in [−1, 1], or null if fewer than 2 non-null pairs are
/// available or if either variable has zero variance.
/// </para>
/// <para>
/// Memory: O(1) per group — only running statistics are maintained.
/// </para>
/// </summary>
public sealed class CorrelationFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "CORR";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("CORR() requires exactly two arguments: y and x.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"CORR() first argument (y) must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"CORR() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CorrelationAccumulator();

    /// <summary>
    /// Online co-moment accumulator that tracks running means, M2 for each
    /// variable, and the cross-moment (co-moment). Pairs where either value
    /// is null are skipped entirely.
    /// </summary>
    private sealed class CorrelationAccumulator : IAggregateAccumulator
    {
        private long _count;
        private double _meanY;
        private double _meanX;
        private double _m2Y;
        private double _m2X;
        private double _coMoment;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull || arguments[1].IsNull) return;

            double y = arguments[0].AsScalar();
            double x = arguments[1].AsScalar();

            _count++;

            double deltaY = y - _meanY;
            _meanY += deltaY / _count;
            double deltaY2 = y - _meanY;
            _m2Y += deltaY * deltaY2;

            double deltaX = x - _meanX;
            _meanX += deltaX / _count;
            double deltaX2 = x - _meanX;
            _m2X += deltaX * deltaX2;

            // Co-moment uses the new mean of X with the old delta of Y.
            _coMoment += deltaY * (x - _meanX);
        }

        public DataValue Result
        {
            get
            {
                if (_count < 2)
                {
                    return DataValue.Null(DataKind.Scalar);
                }

                double denominator = System.Math.Sqrt(_m2Y * _m2X);

                if (denominator == 0.0)
                {
                    return DataValue.Null(DataKind.Scalar);
                }

                return DataValue.FromScalar((float)(_coMoment / denominator));
            }
        }
    }
}
