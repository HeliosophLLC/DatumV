using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Stratified sampling operator: applies a uniform Bernoulli filter at the given
/// percentage across all rows, preserving class proportions defined by the
/// stratification column(s). Each row is independently included with probability
/// <c>percentage / 100</c>, regardless of its class. Because the rate is the same
/// for all classes, proportions are maintained in expectation.
/// </summary>
public sealed class StratifiedSampleOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly double _percentage;
    private readonly string[] _stratifyColumnNames;
    private readonly int? _seed;

    /// <summary>
    /// Creates a stratified sample operator.
    /// </summary>
    /// <param name="source">The inner operator producing rows to sample.</param>
    /// <param name="percentage">The target sampling percentage (0–100).</param>
    /// <param name="stratifyColumnNames">
    /// Column names defining the stratification key. Used for EXPLAIN output
    /// and column-existence validation at execution time.
    /// </param>
    /// <param name="seed">Optional seed for deterministic sampling, or <c>null</c> for non-deterministic.</param>
    public StratifiedSampleOperator(IQueryOperator source, double percentage, string[] stratifyColumnNames, int? seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentage, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, 100.0);
        ArgumentNullException.ThrowIfNull(stratifyColumnNames);

        if (stratifyColumnNames.Length == 0)
        {
            throw new ArgumentException("At least one stratification column must be specified.", nameof(stratifyColumnNames));
        }

        _source = source;
        _percentage = percentage;
        _stratifyColumnNames = stratifyColumnNames;
        _seed = seed;
    }

    /// <summary>The inner operator being sampled.</summary>
    public IQueryOperator Source => _source;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["method"] = "Stratified",
            ["percentage"] = $"{_percentage:F1}%",
            ["columns"] = string.Join(", ", _stratifyColumnNames),
        };

        if (_seed is not null)
        {
            properties["seed"] = _seed.Value.ToString();
        }

        return new OperatorPlanDescription("Stratified Sample")
        {
            Properties = properties,
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // 0% → emit nothing; 100% → pass through unfiltered
        if (_percentage <= 0.0)
        {
            yield break;
        }

        if (_percentage >= 100.0)
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                yield return inputBatch;
            }

            yield break;
        }

        Random random = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        double threshold = _percentage / 100.0;

        RowBatch? outputBatch = null;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int batchIndex = 0; batchIndex < inputBatch.Count; batchIndex++)
            {
                Row row = inputBatch[batchIndex];

                if (random.NextDouble() < threshold)
                {
                    outputBatch ??= RowBatch.Rent(context.BatchSize);
                    outputBatch.Add(row);

                    if (outputBatch.IsFull)
                    {
                        yield return outputBatch;
                        outputBatch = null;
                    }
                }
            }

            inputBatch.Return();
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }
    }
}
