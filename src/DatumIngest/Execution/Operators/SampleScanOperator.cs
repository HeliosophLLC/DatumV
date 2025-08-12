using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Wraps an inner <see cref="IQueryOperator"/> and filters its output stream
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
public sealed class SampleScanOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
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
    public SampleScanOperator(IQueryOperator source, TablesampleMethod method, double percentage, int? seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentage, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, 100.0);

        _source = source;
        _method = method;
        _percentage = percentage;
        _seed = seed;
    }

    /// <summary>The inner operator being sampled.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The sampling strategy.</summary>
    public TablesampleMethod Method => _method;

    /// <summary>The target sampling percentage (0–100).</summary>
    public double Percentage => _percentage;

    /// <summary>The deterministic seed, or <c>null</c> for non-deterministic sampling.</summary>
    public int? Seed => _seed;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
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
    public async IAsyncEnumerable<Row> ExecuteAsync(
        ExecutionContext context)
    {
        // 0% → emit nothing; 100% → pass through unfiltered
        if (_percentage <= 0.0)
        {
            yield break;
        }

        if (_percentage >= 100.0)
        {
            await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                yield return row;
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

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            if (random.NextDouble() < threshold)
            {
                yield return row;
            }
        }
    }
}
