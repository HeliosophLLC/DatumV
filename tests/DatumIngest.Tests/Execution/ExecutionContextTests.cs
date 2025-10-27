using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ExecutionContext"/> cloning and property propagation.
/// </summary>
public sealed class ExecutionContextTests : ServiceTestBase
{
    /// <summary>
    /// Verifies that <see cref="ExecutionContext.WithOuterRow"/> propagates all
    /// init-only properties, including <see cref="ExecutionContext.MaxRecursionDepth"/>.
    /// </summary>
    [Fact]
    public void WithOuterRow_PropagatesAllProperties()
    {
        Row outerRow = new(["x"], [DataValue.FromFloat32(1f)]);
        using ParallelismBudget budget = new(4);
        ExecutionContext original = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            TestTableCatalog.Create(),
            new LocalBufferPool(),
            memoryBudgetBytes: 512)
        {
            MaxRecursionDepth = 42,
            RowLimit = 10,
            DegreeOfParallelism = 8,
            ParallelismBudget = budget,
        };

        ExecutionContext cloned = original.WithOuterRow(outerRow);

        Assert.Equal(outerRow, cloned.OuterRow);
        Assert.Equal(42, cloned.MaxRecursionDepth);
        Assert.Equal(10, cloned.RowLimit);
        Assert.Equal(512L, cloned.MemoryBudgetBytes);
        Assert.Same(original.FunctionRegistry, cloned.FunctionRegistry);
        Assert.Same(original.Catalog, cloned.Catalog);
        Assert.Equal(8, cloned.DegreeOfParallelism);
        Assert.Same(budget, cloned.ParallelismBudget);
    }

    /// <summary>
    /// Verifies that <see cref="ExecutionContext.WithOuterRow"/> uses the default
    /// <see cref="ExecutionContext.MaxRecursionDepth"/> when the original context
    /// was created with the default value.
    /// </summary>
    [Fact]
    public void WithOuterRow_PreservesDefaultMaxRecursionDepth()
    {
        Row outerRow = new(["y"], [DataValue.FromFloat32(2f)]);
        ExecutionContext original = TestExecutionContext.Create();

        ExecutionContext cloned = original.WithOuterRow(outerRow);

        Assert.Equal(1000, cloned.MaxRecursionDepth);
    }
}
