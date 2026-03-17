using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Balanced sampling operator: per-class reservoir sampling (Algorithm R) that
/// returns exactly <c>countPerClass</c> rows from each distinct class (or fewer
/// if the class has fewer rows than the target). The stratification key is
/// defined by one or more column names. Single-pass streaming with bounded
/// memory (one reservoir per distinct class).
/// </summary>
public sealed class BalancedSampleOperator : QueryOperator
{
    /// <summary>
    /// Default maximum number of distinct classes when not configured via
    /// <see cref="ExecutionContext.MaxStratifyClasses"/>.
    /// </summary>
    internal const int DefaultMaxClasses = 10_000;

    private readonly QueryOperator _source;
    private readonly int _countPerClass;
    private readonly string[] _stratifyColumnNames;
    private readonly int? _seed;

    /// <summary>
    /// Creates a balanced sample operator.
    /// </summary>
    /// <param name="source">The inner operator producing rows to sample.</param>
    /// <param name="countPerClass">The target number of rows per distinct class.</param>
    /// <param name="stratifyColumnNames">Column names defining the stratification key.</param>
    /// <param name="seed">Optional seed for deterministic sampling, or <c>null</c> for non-deterministic.</param>
    public BalancedSampleOperator(QueryOperator source, int countPerClass, string[] stratifyColumnNames, int? seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(countPerClass, 1);
        ArgumentNullException.ThrowIfNull(stratifyColumnNames);

        if (stratifyColumnNames.Length == 0)
        {
            throw new ArgumentException("At least one stratification column must be specified.", nameof(stratifyColumnNames));
        }

        _source = source;
        _countPerClass = countPerClass;
        _stratifyColumnNames = stratifyColumnNames;
        _seed = seed;
    }

    /// <summary>The inner operator being sampled.</summary>
    public QueryOperator Source => _source;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["method"] = "Balanced",
            ["count"] = _countPerClass.ToString(),
            ["columns"] = string.Join(", ", _stratifyColumnNames),
        };

        if (_seed is not null)
        {
            properties["seed"] = _seed.Value.ToString();
        }

        return new OperatorPlanDescription("Balanced Sample")
        {
            Properties = properties,
            Children = [(Source, null)],
            Warnings = [$"materializes up to {_countPerClass} rows per class in memory"],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        int maxClasses = context.MaxStratifyClasses ?? DefaultMaxClasses;
        Random random = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;

        Pool pool = context.Pool;

        // Per-class reservoirs keyed by composite stratification key.
        // Uses CompositeKeyComparer for structural value-equality.
        Dictionary<CompositeKey, Reservoir> reservoirs = new(CompositeKeyComparer.Instance);

        // Insertion-order tracking so we emit classes in the order they were first seen.
        List<CompositeKey> classOrder = [];

        // Scratch buffer for evaluating the stratify columns without per-row allocation.
        int keyCount = _stratifyColumnNames.Length;
        DataValue[] keyScratch = new DataValue[keyCount];

        // --- Pass 1: Stream all rows, fill per-class reservoirs ---
        // Each row stored in a reservoir owns its own pool-rented DataValue[] copy
        // bound to context.Store. Without this copy, returning the input batch below
        // would recycle the row's RawValues array out from under the reservoir.

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            try
            {
                for (int batchIndex = 0; batchIndex < inputBatch.Count; batchIndex++)
                {
                    Row row = inputBatch[batchIndex];

                    // Extract the stratify key columns from the row.
                    for (int k = 0; k < keyCount; k++)
                    {
                        keyScratch[k] = row[_stratifyColumnNames[k]];
                    }

                    // Look up or create the reservoir for this class.
                    var lookup = reservoirs.GetAlternateLookup<ReadOnlySpan<DataValue>>();
                    if (!lookup.TryGetValue(keyScratch.AsSpan(), out Reservoir? reservoir))
                    {
                        if (reservoirs.Count >= maxClasses)
                        {
                            throw new InvalidOperationException(
                                $"TABLESAMPLE BALANCED found more than {maxClasses} distinct classes on column '{string.Join(", ", _stratifyColumnNames)}', " +
                                $"exceeding the maximum of {maxClasses}. Use a less granular stratification column or increase the limit.");
                        }

                        CompositeKey newKey = new(keyScratch.AsSpan().ToArray());
                        reservoir = new Reservoir(_countPerClass);
                        reservoirs[newKey] = reservoir;
                        classOrder.Add(newKey);
                    }

                    // Algorithm R: reservoir sampling.
                    reservoir.SeenCount++;

                    if (reservoir.Count < _countPerClass)
                    {
                        // Reservoir not yet full — copy and add directly.
                        DataValue[] copy = pool.RentAndCopyDataValues(row, inputBatch.Arena, context.Store);
                        reservoir.Rows[reservoir.Count] = new Row(row.ColumnLookup, copy);
                        reservoir.Count++;
                    }
                    else
                    {
                        // Reservoir full — replace a random element with decreasing probability.
                        int j = random.Next(reservoir.SeenCount);
                        if (j < _countPerClass)
                        {
                            // Return the displaced row's pool-rented array before overwriting,
                            // otherwise the long-running pass would steadily leak DataValue[]
                            // arrays for every replacement decision.
                            pool.ReturnRow(reservoir.Rows[j]);
                            DataValue[] copy = pool.RentAndCopyDataValues(row, inputBatch.Arena, context.Store);
                            reservoir.Rows[j] = new Row(row.ColumnLookup, copy);
                        }
                    }
                }
            }
            finally
            {
                context.ReturnRowBatch(inputBatch);
            }
        }

        // --- Pass 2: Emit all reservoirs in class-first order ---
        // Each reservoir row already owns its DataValue[] (pool-rented during pass 1),
        // so handing it to outputBatch transfers ownership cleanly — when the consumer
        // returns the output batch the array goes back to the pool exactly once.

        RowBatch? outputBatch = null;

        foreach (CompositeKey key in classOrder)
        {
            Reservoir reservoir = reservoirs[key];

            for (int i = 0; i < reservoir.Count; i++)
            {
                Row row = reservoir.Rows[i];
                outputBatch ??= context.RentRowBatch(row.ColumnLookup);
                outputBatch.Add(row.RawValues);

                if (outputBatch.IsFull)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }
            }
        }

        if (outputBatch is not null)
        {
            RowBatch toYield = outputBatch;
            outputBatch = null;
            yield return toYield;
        }
    }

    /// <summary>
    /// Per-class reservoir state. Holds up to <c>capacity</c> rows and tracks
    /// the total number of rows seen for this class (needed for Algorithm R).
    /// </summary>
    private sealed class Reservoir
    {
        public readonly Row[] Rows;
        public int Count;
        public int SeenCount;

        public Reservoir(int capacity)
        {
            Rows = new Row[capacity];
        }
    }
}
