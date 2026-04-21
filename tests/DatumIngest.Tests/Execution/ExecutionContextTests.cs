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
    /// Verifies that <see cref="ExecutionContext.Derive"/> with an outer row
    /// propagates all init-only properties, including
    /// <see cref="ExecutionContext.MaxRecursionDepth"/>.
    /// </summary>
    [Fact]
    public void Derive_WithOuterRow_PropagatesAllProperties()
    {
        Row outerRow = MakeRow(["x"], DataValue.FromFloat32(1f));
        ExecutionContext original = new DatumIngest.Execution.ExecutionContext(
            CreateCatalog(),
            memoryBudgetBytes: 512)
        {
            MaxRecursionDepth = 42,
            RowLimit = 10,
            DegreeOfParallelism = 8,
        };

        ExecutionContext cloned = original.Derive(outerRow: outerRow);

        Assert.Equal(outerRow, cloned.OuterRow);
        Assert.Equal(42, cloned.MaxRecursionDepth);
        Assert.Equal(10, cloned.RowLimit);
        Assert.Equal(512L, cloned.MemoryBudgetBytes);
        Assert.Same(original.FunctionRegistry, cloned.FunctionRegistry);
        Assert.Same(original.Catalog, cloned.Catalog);
        Assert.Equal(8, cloned.DegreeOfParallelism);
    }

    /// <summary>
    /// Verifies that <see cref="ExecutionContext.Derive"/> uses the default
    /// <see cref="ExecutionContext.MaxRecursionDepth"/> when the original context
    /// was created with the default value.
    /// </summary>
    [Fact]
    public void Derive_PreservesDefaultMaxRecursionDepth()
    {
        Row outerRow = MakeRow(["y"], DataValue.FromFloat32(2f));
        ExecutionContext original = CreateExecutionContext();

        ExecutionContext cloned = original.Derive(outerRow: outerRow);

        Assert.Equal(1000, cloned.MaxRecursionDepth);
    }

    /// <summary>
    /// A primary-constructed context owns its accountant and disposes it.
    /// </summary>
    [Fact]
    public void Dispose_OwnedAccountant_IsReleased()
    {
        ExecutionContext context = new DatumIngest.Execution.ExecutionContext(CreateCatalog());

        MemoryAccountant accountant = context.Accountant;
        context.Dispose();

        // Idempotent — second dispose must not throw.
        accountant.Dispose();
    }

    /// <summary>
    /// Copy-constructed contexts borrow the parent's accountant; disposing the
    /// child must not tear down the parent's accountant.
    /// </summary>
    [Fact]
    public void Derive_ChildContextSharesAccountant_AndDoesNotDisposeIt()
    {
        Row outerRow = MakeRow(["x"], DataValue.FromFloat32(1f));
        using ExecutionContext parent = new DatumIngest.Execution.ExecutionContext(
            CreateCatalog(),
            memoryBudgetBytes: 1000);

        ExecutionContext child = parent.Derive(outerRow: outerRow);
        Assert.Same(parent.Accountant, child.Accountant);

        parent.Accountant.NotifyMaterialized(500);
        Assert.Equal(500, child.Accountant.CurrentResidentBytes);

        child.Dispose();
        // Parent's accountant must still be usable.
        parent.Accountant.NotifyMaterialized(100);
        Assert.Equal(600, parent.Accountant.CurrentResidentBytes);
    }

    /// <summary>
    /// When a context is given an existing accountant it borrows rather than owns;
    /// disposing the context leaves the caller's accountant intact.
    /// </summary>
    [Fact]
    public void Dispose_BorrowedAccountant_IsNotReleased()
    {
        using MemoryAccountant borrowed = new(memoryBudgetBytes: 1000);
        borrowed.NotifyMaterialized(200);

        ExecutionContext context = new DatumIngest.Execution.ExecutionContext(
            CreateCatalog(),
            accountant: borrowed);
        context.Dispose();

        // Borrowed accountant is still alive.
        borrowed.NotifyMaterialized(100);
        Assert.Equal(300, borrowed.CurrentResidentBytes);
    }
}
