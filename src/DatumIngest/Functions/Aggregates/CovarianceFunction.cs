using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>COVAR_POP(y, x)</c> and <c>COVAR_SAMP(y, x)</c>. Computes
/// the covariance between two numeric columns using an online co-moment
/// algorithm (parallel to Welford's variance algorithm).
/// <para>
/// Population covariance divides by N; sample covariance divides by N−1.
/// Returns null if all values are null, or if sample covariance is requested
/// with fewer than two non-null pairs.
/// </para>
/// <para>
/// Memory: O(1) per group — only running statistics are maintained.
/// </para>
/// </summary>
public sealed class CovarianceFunction : IAggregateFunction
{
    private readonly bool _usePopulation;

    /// <summary>
    /// Creates a covariance aggregate function.
    /// </summary>
    /// <param name="usePopulation">
    /// When <see langword="true"/>, computes population covariance (divide by N).
    /// When <see langword="false"/>, computes sample covariance (divide by N−1).
    /// </param>
    /// <param name="name">The SQL function name (e.g. "COVAR_POP", "COVAR_SAMP").</param>
    public CovarianceFunction(bool usePopulation, string name)
    {
        _usePopulation = usePopulation;
        Name = name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: y and x.");
        }

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"{Name}() first argument (y) must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"{Name}() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CovarianceAccumulator(_usePopulation);

    /// <summary>
    /// Online co-moment accumulator. Tracks running means for both variables
    /// and the cross-moment. Pairs where either value is null are skipped.
    /// </summary>
    private sealed class CovarianceAccumulator : IAggregateAccumulator
    {
        private readonly bool _usePopulation;
        private long _count;
        private double _meanY;
        private double _meanX;
        private double _coMoment;

        public CovarianceAccumulator(bool usePopulation)
        {
            _usePopulation = usePopulation;
        }

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull || arguments[1].IsNull) return;

            double y = arguments[0].AsFloat32();
            double x = arguments[1].AsFloat32();

            _count++;

            double deltaY = y - _meanY;
            _meanY += deltaY / _count;

            double deltaX = x - _meanX;
            _meanX += deltaX / _count;

            // Co-moment uses the new mean of X with the old delta of Y.
            _coMoment += deltaY * (x - _meanX);
        }

        public DataValue Result
        {
            get
            {
                if (_usePopulation)
                {
                    return _count > 0
                        ? DataValue.FromFloat32((float)(_coMoment / _count))
                        : DataValue.Null(DataKind.Float32);
                }

                return _count > 1
                    ? DataValue.FromFloat32((float)(_coMoment / (_count - 1)))
                    : DataValue.Null(DataKind.Float32);
            }
        }
    }
}
