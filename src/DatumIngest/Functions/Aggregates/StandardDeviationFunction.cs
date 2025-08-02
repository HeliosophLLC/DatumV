using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>STDDEV(expression)</c>, <c>STDDEV_SAMP(expression)</c>, and
/// <c>STDDEV_POP(expression)</c>. Computes the standard deviation of all non-null
/// numeric values using Welford's online algorithm (O(1) memory per group).
/// <para>
/// Sample standard deviation (N−1 denominator) is used by <c>STDDEV</c> and <c>STDDEV_SAMP</c>.
/// Population standard deviation (N denominator) is used by <c>STDDEV_POP</c>.
/// Returns null if all values are null, or if sample standard deviation is
/// requested with fewer than two values.
/// </para>
/// </summary>
public sealed class StandardDeviationFunction : IAggregateFunction
{
    private readonly bool _usePopulation;

    /// <summary>
    /// Creates a standard deviation aggregate function.
    /// </summary>
    /// <param name="usePopulation">
    /// When <see langword="true"/>, computes population stddev (divide by N).
    /// When <see langword="false"/>, computes sample stddev (divide by N−1).
    /// </param>
    /// <param name="name">The SQL function name (e.g. "STDDEV", "STDDEV_POP").</param>
    public StandardDeviationFunction(bool usePopulation, string name)
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
    public IAggregateAccumulator CreateAccumulator() => new WelfordStandardDeviationAccumulator(_usePopulation);

    /// <summary>
    /// Welford's online algorithm accumulator that returns the square root of
    /// variance as the final result.
    /// </summary>
    private sealed class WelfordStandardDeviationAccumulator : IAggregateAccumulator
    {
        private readonly bool _usePopulation;
        private long _count;
        private double _mean;
        private double _m2;

        public WelfordStandardDeviationAccumulator(bool usePopulation)
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
                        ? DataValue.FromScalar((float)System.Math.Sqrt(_m2 / _count))
                        : DataValue.Null(DataKind.Scalar);
                }

                // Sample stddev requires at least 2 values (N-1 denominator).
                return _count > 1
                    ? DataValue.FromScalar((float)System.Math.Sqrt(_m2 / (_count - 1)))
                    : DataValue.Null(DataKind.Scalar);
            }
        }
    }
}
