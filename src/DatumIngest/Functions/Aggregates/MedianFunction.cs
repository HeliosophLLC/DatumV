using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>MEDIAN(expression)</c>. Computes the median (50th percentile)
/// of all non-null numeric values. For even-count groups, returns the average
/// of the two middle values (continuous interpolation).
/// Returns null if all values are null.
/// <para>
/// Memory: O(N) per group — all non-null values are collected before the median
/// is computed. This is acceptable for typical ML group sizes (categories,
/// classes, time buckets).
/// </para>
/// </summary>
public sealed class MedianFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "MEDIAN";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Exact median of non-null numeric values; averages the two middle values when the count is even.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MEDIAN() requires exactly one argument.");
        }

        bool isNumeric = argumentKinds[0] is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64;
        if (!isNumeric)
        {
            throw new ArgumentException($"MEDIAN() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new MedianAccumulator();

    private sealed class MedianAccumulator : IAggregateAccumulator
    {
        private readonly List<double> _values = [];

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull) return;

            _values.Add(arguments[0].ToDouble());
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            MedianAccumulator otherAccumulator = (MedianAccumulator)other;
            _values.AddRange(otherAccumulator._values);
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_values.Count == 0)
            {
                return new(DataValue.Null(DataKind.Float64));
            }

            _values.Sort();
            int count = _values.Count;
            int mid = count / 2;

            if (count % 2 == 1)
            {
                return new(DataValue.FromFloat64(_values[mid]));
            }

            // Even count: average of the two middle values.
            double median = (_values[mid - 1] + _values[mid]) / 2.0;
            return new(DataValue.FromFloat64(median));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _values.Clear();
        }
    }
}
