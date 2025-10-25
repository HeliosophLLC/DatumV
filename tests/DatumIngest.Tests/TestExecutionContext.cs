using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests;

/// <summary>
/// Factory for creating <see cref="ExecutionContext"/> instances in tests.
/// Centralises construction so that adding new required parameters to
/// <see cref="ExecutionContext"/> only requires a single change.
/// </summary>
internal static class TestExecutionContext
{
    /// <summary>
    /// Creates a minimal execution context suitable for most unit tests.
    /// Uses a fresh <see cref="Arena"/> as the value store.
    /// </summary>
    internal static ExecutionContext Create(
        FunctionRegistry? functionRegistry = null,
        TableCatalog? catalog = null,
        long? memoryBudgetBytes = null,
        QueryMeter? meter = null)
    {
        return new ExecutionContext(
            CancellationToken.None,
            functionRegistry ?? FunctionRegistry.CreateDefault(),
            catalog ?? new TableCatalog(new Pool(GlobalPool.Backing)),
            new LocalBufferPool(),
            queryMeter: meter,
            memoryBudgetBytes: memoryBudgetBytes);
    }
}
