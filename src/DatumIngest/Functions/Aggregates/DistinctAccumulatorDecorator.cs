using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Decorator that wraps an <see cref="IAggregateAccumulator"/> with a
/// <see cref="HashSet{T}"/> filter, ensuring only distinct argument values
/// are forwarded to the inner accumulator. This enables <c>COUNT(DISTINCT col)</c>,
/// <c>SUM(DISTINCT col)</c>, and similar aggregate DISTINCT semantics without
/// modifying the individual aggregate function implementations.
/// <para>
/// For single-argument aggregates (the common case), a <see cref="HashSet{DataValue}"/>
/// is used. For multi-argument aggregates, a <see cref="HashSet{CompositeKey}"/> provides
/// element-wise equality.
/// </para>
/// </summary>
internal sealed class DistinctAccumulatorDecorator : IAggregateAccumulator
{
    private readonly IAggregateAccumulator _inner;
    private readonly HashSet<DataValue>? _singleArgumentSet;
    private readonly HashSet<CompositeKey>? _multiArgumentSet;

    /// <summary>
    /// Creates a new distinct decorator wrapping the given accumulator.
    /// </summary>
    /// <param name="inner">The accumulator to delegate to for distinct values.</param>
    /// <param name="argumentCount">
    /// The number of arguments the aggregate function expects.
    /// Determines whether single-key or composite-key deduplication is used.
    /// </param>
    public DistinctAccumulatorDecorator(IAggregateAccumulator inner, int argumentCount)
    {
        _inner = inner;

        if (argumentCount <= 1)
        {
            _singleArgumentSet = new HashSet<DataValue>();
        }
        else
        {
            _multiArgumentSet = new HashSet<CompositeKey>();
        }
    }

    /// <inheritdoc />
    public void Accumulate(ReadOnlySpan<DataValue> arguments)
    {
        bool isNew;

        if (_singleArgumentSet is not null)
        {
            // Single-argument path: deduplicate on the argument value directly.
            // COUNT(DISTINCT col) with no arguments (COUNT(*)) should never reach here
            // because COUNT(DISTINCT *) is rejected during validation.
            DataValue key = arguments.Length > 0 ? arguments[0] : DataValue.Null(DataKind.Float32);
            isNew = _singleArgumentSet.Add(key);
        }
        else
        {
            // Multi-argument path (rare): deduplicate on the composite key.
            DataValue[] parts = new DataValue[arguments.Length];
            arguments.CopyTo(parts);
            isNew = _multiArgumentSet!.Add(new CompositeKey(parts));
        }

        if (isNew)
        {
            _inner.Accumulate(arguments);
        }
    }

    /// <inheritdoc />
    public DataValue Result => _inner.Result;
}
