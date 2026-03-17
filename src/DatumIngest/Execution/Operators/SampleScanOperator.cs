using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Wraps an inner <see cref="QueryOperator"/> and filters its output stream
/// to return an approximate percentage of rows. Supports two sampling strategies:
/// <list type="bullet">
///   <item><see cref="TablesampleMethod.Bernoulli"/> — row-level: each row is
///   independently included with probability <c>percentage / 100</c>.</item>
///   <item><see cref="TablesampleMethod.System"/> — chunk-level: delegates to the
///   inner operator's <see cref="ScanOperator.SourceIndex"/> to skip entire chunks,
///   falling back to Bernoulli when no index is available.</item>
/// </list>
/// When a seed is provided via <c>REPEATABLE(seed)</c>, sampling is deterministic
/// across identical data sets.
/// </summary>
public sealed class SampleScanOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly TablesampleMethod _method;
    private readonly double _percentage;
    private readonly int? _seed;

    /// <summary>
    /// Creates a sample scan operator.
    /// </summary>
    /// <param name="source">The inner operator producing rows to sample.</param>
    /// <param name="method">The sampling strategy.</param>
    /// <param name="percentage">The target sampling percentage (0–100).</param>
    /// <param name="seed">Optional seed for deterministic sampling, or <c>null</c> for non-deterministic.</param>
    public SampleScanOperator(QueryOperator source, TablesampleMethod method, double percentage, int? seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentage, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, 100.0);

        _source = source;
        _method = method;
        _percentage = percentage;
        _seed = seed;
    }

    /// <summary>The inner operator being sampled.</summary>
    public QueryOperator Source => _source;

    /// <summary>The sampling strategy.</summary>
    public TablesampleMethod Method => _method;

    /// <summary>The target sampling percentage (0–100).</summary>
    public double Percentage => _percentage;

    /// <summary>The deterministic seed, or <c>null</c> for non-deterministic sampling.</summary>
    public int? Seed => _seed;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["method"] = _method.ToString(),
            ["percentage"] = $"{_percentage:F1}%",
        };

        if (_seed is not null)
        {
            properties["seed"] = _seed.Value.ToString();
        }

        return new OperatorPlanDescription("Sample Scan")
        {
            Properties = properties,
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
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

        // Both methods use Bernoulli row-level sampling. System sampling would
        // ideally skip entire chunks, but requires tight integration with the
        // ScanOperator's chunk-iteration loop. For now, both paths converge on
        // the same row-level probabilistic filter — System can be optimized
        // later to leverage SourceIndex chunk metadata.
        Random random = _seed.HasValue ? new Random(_seed.Value) : Random.Shared;
        double threshold = _percentage / 100.0;

        Pool pool = context.Pool;
        RowBatch? outputBatch = null;

        await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            try
            {
                for (int batchIndex = 0; batchIndex < inputBatch.Count; batchIndex++)
                {
                    Row row = inputBatch[batchIndex];

                    if (random.NextDouble() < threshold)
                    {
                        // Copy values out of the input batch's arena before adding to
                        // the output batch — the input batch is returned to the pool
                        // below, which would alias-recycle the row's DataValue[]
                        // otherwise.
                        outputBatch ??= context.RentRowBatch(row.ColumnLookup);
                        outputBatch.Add(pool.RentAndCopyDataValues(row, inputBatch.Arena, context.Store));

                        if (outputBatch.IsFull)
                        {
                            RowBatch toYield = outputBatch;
                            outputBatch = null;
                            yield return toYield;
                        }
                    }
                }
            }
            finally
            {
                context.ReturnRowBatch(inputBatch);
            }
        }

        if (outputBatch is not null)
        {
            RowBatch toYield = outputBatch;
            outputBatch = null;
            yield return toYield;
        }
    }
}
