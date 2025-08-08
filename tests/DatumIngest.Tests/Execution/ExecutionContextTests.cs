using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ExecutionContext"/> cloning and property propagation.
/// </summary>
public sealed class ExecutionContextTests
{
    /// <summary>
    /// Verifies that <see cref="ExecutionContext.WithOuterRow"/> propagates all
    /// init-only properties, including <see cref="ExecutionContext.MaxRecursionDepth"/>.
    /// </summary>
    [Fact]
    public void WithOuterRow_PropagatesAllProperties()
    {
        Row outerRow = new(["x"], [DataValue.FromScalar(1f)]);
        ExecutionContext original = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            memoryBudgetBytes: 512)
        {
            MaxRecursionDepth = 42,
            RowLimit = 10,
        };

        ExecutionContext cloned = original.WithOuterRow(outerRow);

        Assert.Same(outerRow, cloned.OuterRow);
        Assert.Equal(42, cloned.MaxRecursionDepth);
        Assert.Equal(10, cloned.RowLimit);
        Assert.Equal(512L, cloned.MemoryBudgetBytes);
        Assert.Same(original.FunctionRegistry, cloned.FunctionRegistry);
        Assert.Same(original.Catalog, cloned.Catalog);
    }

    /// <summary>
    /// Verifies that <see cref="ExecutionContext.WithOuterRow"/> uses the default
    /// <see cref="ExecutionContext.MaxRecursionDepth"/> when the original context
    /// was created with the default value.
    /// </summary>
    [Fact]
    public void WithOuterRow_PreservesDefaultMaxRecursionDepth()
    {
        Row outerRow = new(["y"], [DataValue.FromScalar(2f)]);
        ExecutionContext original = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog());

        ExecutionContext cloned = original.WithOuterRow(outerRow);

        Assert.Equal(1000, cloned.MaxRecursionDepth);
    }
}
