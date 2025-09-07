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

        bool isNumeric = argumentKinds[0] is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64;
        if (!isNumeric)
        {
            throw new ArgumentException($"{Name}() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
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

            double value = AvgFunction.ExtractAsDouble(arguments[0]);
            _count++;
            double delta = value - _mean;
            _mean += delta / _count;
            double delta2 = value - _mean;
            _m2 += delta * delta2;
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            WelfordStandardDeviationAccumulator otherAccumulator = (WelfordStandardDeviationAccumulator)other;

            if (otherAccumulator._count == 0)
            {
                return;
            }

            if (_count == 0)
            {
                _count = otherAccumulator._count;
                _mean = otherAccumulator._mean;
                _m2 = otherAccumulator._m2;
                return;
            }

            long combinedCount = _count + otherAccumulator._count;
            double delta = otherAccumulator._mean - _mean;
            _m2 += otherAccumulator._m2 + delta * delta * _count * otherAccumulator._count / combinedCount;
            _mean += delta * otherAccumulator._count / combinedCount;
            _count = combinedCount;
        }

        public DataValue Result
        {
            get
            {
                if (_usePopulation)
                {
                    return _count > 0
                        ? DataValue.FromFloat64(System.Math.Sqrt(_m2 / _count))
                        : DataValue.Null(DataKind.Float64);
                }

                // Sample stddev requires at least 2 values (N-1 denominator).
                return _count > 1
                    ? DataValue.FromFloat64(System.Math.Sqrt(_m2 / (_count - 1)))
                    : DataValue.Null(DataKind.Float64);
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            _count = 0;
            _mean = 0;
            _m2 = 0;
        }
    }
}
