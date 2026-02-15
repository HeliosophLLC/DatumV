using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>APPROX_MEDIAN(expression)</c>. Computes an approximate median
/// (50th percentile) using Algorithm R reservoir sampling with a configurable
/// maximum sample size. For groups smaller than the reservoir cap, the result
/// is exact; for larger groups, the error is typically 1–5%.
/// <para>
/// This provides O(1) memory per group (bounded by <see cref="MaxSamples"/>)
/// regardless of group size, compared to the exact <c>MEDIAN</c> which is O(N).
/// </para>
/// </summary>
public sealed class ApproximateMedianFunction : IAggregateFunction
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    internal const int MaxSamples = 100_000;

    /// <inheritdoc/>
    public string Name => "APPROX_MEDIAN";

    /// <inheritdoc/>
    // Reservoir sampling up to 100K samples with O(N log N) sort at finalization — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("APPROX_MEDIAN() requires exactly one argument.");
        }

        if (!PercentileDiscreteFunction.IsNumericKind(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"APPROX_MEDIAN() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ReservoirMedianAccumulator();

    /// <summary>
    /// Algorithm R reservoir sampling accumulator for approximate median computation.
    /// Retains up to <see cref="MaxSamples"/> values; when the reservoir is full,
    /// each new value has a <c>MaxSamples / totalCount</c> probability of replacing
    /// an existing sample.
    /// </summary>
    private sealed class ReservoirMedianAccumulator : IAggregateAccumulator
    {
        private readonly List<double> _samples = [];
        private readonly Random _random = new(42);
        private long _totalCount;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull) return;

            double value = PercentileDiscreteFunction.ToDouble(arguments[0]);
            _totalCount++;

            if (_samples.Count < MaxSamples)
            {
                _samples.Add(value);
            }
            else
            {
                long j = _random.NextInt64(_totalCount);

                if (j < MaxSamples)
                {
                    _samples[(int)j] = value;
                }
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            ReservoirMedianAccumulator otherAccumulator = (ReservoirMedianAccumulator)other;
            _totalCount += otherAccumulator._totalCount;
            _samples.AddRange(otherAccumulator._samples);

            if (_samples.Count > MaxSamples)
            {
                // Shuffle and truncate to maintain reservoir invariant.
                for (int i = _samples.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    (_samples[i], _samples[j]) = (_samples[j], _samples[i]);
                }

                _samples.RemoveRange(MaxSamples, _samples.Count - MaxSamples);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_samples.Count == 0)
            {
                return new(DataValue.Null(DataKind.Float64));
            }

            _samples.Sort();

            int count = _samples.Count;
            int mid = count / 2;

            if (count % 2 == 1)
            {
                return new(DataValue.FromFloat64(_samples[mid]));
            }

            double median = (_samples[mid - 1] + _samples[mid]) / 2.0;
            return new(DataValue.FromFloat64(median));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _samples.Clear();
            _totalCount = 0;
        }
    }
}
