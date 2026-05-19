using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// Implements <c>MAX(expression)</c>. Returns the maximum non-null value.
/// Works on numeric, string, date, datetime, and time types using their
/// natural ordering. Returns null if all values are null.
/// </summary>
public sealed class MaxFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "MAX";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Maximum non-null value in a group; works on any comparable kind (numeric, string, date, datetime, time).";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MAX() requires exactly one argument.");
        }

        DataKind kind = argumentKinds[0];
        if (!DataValueComparer.IsComparable(kind))
        {
            throw new ArgumentException($"MAX() requires a comparable argument, got {kind}.");
        }

        return kind;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new MaxAccumulator();

    private sealed class MaxAccumulator : IAggregateAccumulator
    {
        private DataValue? _maximum;
        // Captured from the first non-null value so that the empty-group null
        // carries the correct input kind rather than an arbitrary default.
        private DataKind _inputKind = DataKind.Float64;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

            _inputKind = value.Kind;

            // _maximum was stabilised into frame.Target by a previous Accumulate (or is
            // null on the first call). New candidate's payload still lives in frame.Source.
            // Compare across the two stores, then stabilise the new winner into Target so
            // the captured DataValue's offsets stay valid after the input batch's arena
            // is recycled.
            if (_maximum is null
                || DataValueComparer.Compare(value, frame.Source, _maximum.Value, frame.Target) > 0)
            {
                _maximum = DataValueRetention.Stabilize(value, frame.Source, frame.Target);
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            MaxAccumulator otherAccumulator = (MaxAccumulator)other;

            if (otherAccumulator._maximum is null)
            {
                return ValueTask.CompletedTask;
            }

            _inputKind = otherAccumulator._inputKind;
            // Both sides' captured values were Stabilized into the same Target store
            // during their Accumulate calls (per the parallel-aggregate contract:
            // workers share context.Store). Compare against that shared store.
            if (_maximum is null
                || DataValueComparer.Compare(otherAccumulator._maximum.Value, _maximum.Value, frame.Target) > 0)
            {
                _maximum = otherAccumulator._maximum;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_maximum is null) return new(DataValue.Null(_inputKind));
            // _maximum lives in the Target arena passed during Accumulate (typically
            // context.Store). Restabilise into the emit Target so result-batch readers
            // resolve against the right arena.
            return new(DataValueRetention.Stabilize(_maximum.Value, frame.Source, frame.Target));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _maximum = null;
            _inputKind = DataKind.Float64;
        }
    }
}
