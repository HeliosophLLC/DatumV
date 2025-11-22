using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests;

/// <summary>
/// A simple mock data-source operator backed by an <see cref="InMemoryTableProvider"/>.
/// Used in operator unit tests that want to feed a known row set into a downstream
/// operator without going through the catalog/planner path.
/// </summary>
/// <remarks>
/// Construct via <see cref="ServiceTestBase.CreateMockOperator(string[], object[][])"/>
/// so the provider is backed by the test's DI-resolved <see cref="Pooling.Pool"/>. The
/// operator delegates scanning to <see cref="InMemoryTableProvider.ScanAsync"/>, so
/// batches are pool-rented and carry an arena — matching production scan semantics.
/// </remarks>
public sealed class MockOperator : IQueryOperator
{
    private readonly InMemoryTableProvider _provider;

    public MockOperator(InMemoryTableProvider provider)
    {
        _provider = provider;
    }

    public OperatorPlanDescription DescribeForExplain() => new("Mock");

    public IAsyncEnumerable<RowBatch> ExecuteAsync(DatumIngest.Execution.ExecutionContext context)
        => _provider.ScanAsync(requiredColumns: null, filterHint: null, context.Store, context.CancellationToken);
}