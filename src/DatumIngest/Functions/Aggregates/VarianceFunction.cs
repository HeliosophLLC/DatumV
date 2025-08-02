using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>VARIANCE(expression)</c>, <c>VAR_SAMP(expression)</c>, and
/// <c>VAR_POP(expression)</c>. Computes variance of all non-null numeric values
/// using Welford's online algorithm (O(1) memory per group).
/// <para>
/// Sample variance (N−1 denominator) is used by <c>VARIANCE</c> and <c>VAR_SAMP</c>.
/// Population variance (N denominator) is used by <c>VAR_POP</c>.
/// Returns null if all values are null, or if sample variance is requested
/// with fewer than two values.
/// </para>
/// </summary>
public sealed class VarianceFunction : IAggregateFunction
{
    private readonly bool _usePopulation;

    /// <summary>
    /// Creates a variance aggregate function.
    /// </summary>
    /// <param name="usePopulation">
    /// When <see langword="true"/>, computes population variance (divide by N).
    /// When <see langword="false"/>, computes sample variance (divide by N−1).
    /// </param>
    /// <param name="name">The SQL function name (e.g. "VARIANCE", "VAR_POP").</param>
    public VarianceFunction(bool usePopulation, string name)
    {
        _usePopulation = usePopulation;
        Name = name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException($"{Name}() requires exactly one argument.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException($"{Name}() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new WelfordVarianceAccumulator(_usePopulation);

    /// <summary>
    /// Welford's online algorithm for numerically stable variance computation.
    /// Tracks count, running mean, and sum of squared deviations (M2).
    /// </summary>
    private sealed class WelfordVarianceAccumulator : IAggregateAccumulator
    {
        private readonly bool _usePopulation;
        private long _count;
        private double _mean;
        private double _m2;

        public WelfordVarianceAccumulator(bool usePopulation)
        {
            _usePopulation = usePopulation;
        }

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            double value = arguments[0].AsScalar();
            _count++;
            double delta = value - _mean;
            _mean += delta / _count;
            double delta2 = value - _mean;
            _m2 += delta * delta2;
        }

        public DataValue Result
        {
            get
            {
                if (_usePopulation)
                {
                    return _count > 0
                        ? DataValue.FromScalar((float)(_m2 / _count))
                        : DataValue.Null(DataKind.Scalar);
                }

                // Sample variance requires at least 2 values (N-1 denominator).
                return _count > 1
                    ? DataValue.FromScalar((float)(_m2 / (_count - 1)))
                    : DataValue.Null(DataKind.Scalar);
            }
        }
    }
}
