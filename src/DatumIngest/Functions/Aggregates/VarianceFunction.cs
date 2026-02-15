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

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull) return;

            double value = arguments[0].ToDouble();
            _count++;
            double delta = value - _mean;
            _mean += delta / _count;
            double delta2 = value - _mean;
            _m2 += delta * delta2;
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            WelfordVarianceAccumulator otherAccumulator = (WelfordVarianceAccumulator)other;

            if (otherAccumulator._count == 0)
            {
                return ValueTask.CompletedTask;
            }

            if (_count == 0)
            {
                _count = otherAccumulator._count;
                _mean = otherAccumulator._mean;
                _m2 = otherAccumulator._m2;
                return ValueTask.CompletedTask;
            }

            long combinedCount = _count + otherAccumulator._count;
            double delta = otherAccumulator._mean - _mean;
            _m2 += otherAccumulator._m2 + delta * delta * _count * otherAccumulator._count / combinedCount;
            _mean += delta * otherAccumulator._count / combinedCount;
            _count = combinedCount;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_usePopulation)
            {
                return new(_count > 0
                    ? DataValue.FromFloat64(_m2 / _count)
                    : DataValue.Null(DataKind.Float64));
            }

            // Sample variance requires at least 2 values (N-1 denominator).
            return new(_count > 1
                ? DataValue.FromFloat64(_m2 / (_count - 1))
                : DataValue.Null(DataKind.Float64));
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
