using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests;

/// <summary>
/// A mock data-source operator that invokes a callback for each row yielded.
/// Used to verify a consumer does not read more rows than necessary (e.g. LIMIT).
/// </summary>
/// <remarks>
/// Construct via <see cref="ServiceTestBase.CreateCountingOperator"/>. The callback
/// fires per row as batches are pulled from the underlying
/// <see cref="InMemoryTableProvider"/>; when the consumer stops pulling, no further
/// rows are materialized — preserving the original CountingOperator semantics.
/// </remarks>
public sealed class CountingOperator : QueryOperator
{
    private readonly InMemoryTableProvider _provider;
    private readonly Action _onRowYielded;

    public CountingOperator(InMemoryTableProvider provider, Action onRowYielded)
    {
        _provider = provider;
        _onRowYielded = onRowYielded;
    }

    protected override OperatorPlanDescription DescribeForExplainImpl() => new("Counting Mock");

    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(Heliosoph.DatumV.Execution.ExecutionContext context)
    {
        await foreach (RowBatch batch in _provider.ScanAsync(
            requiredColumns: null, filterHint: null, context.Store, context.CancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                _onRowYielded();
            }
            yield return batch;
        }
    }
}