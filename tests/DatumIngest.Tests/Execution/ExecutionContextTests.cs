using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

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
        ExecutionContext original = new Heliosoph.DatumV.Execution.ExecutionContext(
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
        ExecutionContext context = new Heliosoph.DatumV.Execution.ExecutionContext(CreateCatalog());

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
        using ExecutionContext parent = new Heliosoph.DatumV.Execution.ExecutionContext(
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

        ExecutionContext context = new Heliosoph.DatumV.Execution.ExecutionContext(
            CreateCatalog(),
            accountant: borrowed);
        context.Dispose();

        // Borrowed accountant is still alive.
        borrowed.NotifyMaterialized(100);
        Assert.Equal(300, borrowed.CurrentResidentBytes);
    }

    /// <summary>
    /// Verifies that <see cref="ExecutionContext.Derive"/> carries
    /// <see cref="ExecutionContext.ProcedureCallDepth"/> and
    /// <see cref="ExecutionContext.PrintSink"/> through to the child context.
    /// </summary>
    [Fact]
    public void Derive_PropagatesProcedureCallDepthAndPrintSink()
    {
        List<string?> captured = [];
        ExecutionContext original = CreateExecutionContext(
            procedureCallDepth: 2,
            printSink: captured.Add);

        ExecutionContext derived = original.Derive();

        Assert.Equal(2, derived.ProcedureCallDepth);
        Assert.Same(original.PrintSink, derived.PrintSink);

        derived.PrintSink("hello");
        Assert.Equal("hello", Assert.Single(captured));
    }

    /// <summary>
    /// A freshly constructed context reports depth 0 and a non-null no-op
    /// PrintSink — top-level callers invoke the sink without a null check
    /// and the PRINT silently drops.
    /// </summary>
    [Fact]
    public void DefaultContext_HasZeroDepthAndNoOpPrintSink()
    {
        using ExecutionContext ctx = new Heliosoph.DatumV.Execution.ExecutionContext(CreateCatalog());
        Assert.Equal(0, ctx.ProcedureCallDepth);
        Assert.NotNull(ctx.PrintSink);
        ctx.PrintSink("dropped");   // no-op; must not throw
    }

    /// <summary>
    /// Every context allocates a procedure-lifetime variable substrate
    /// (arena + scope chain) — single-statement and multi-statement
    /// execution share the same shape, the former simply never declares
    /// into it. The scope starts with one root frame already pushed.
    /// </summary>
    [Fact]
    public void DefaultContext_AllocatesVariableSubstrate()
    {
        using ExecutionContext ctx = new Heliosoph.DatumV.Execution.ExecutionContext(CreateCatalog());

        Assert.Equal(1, ctx.VariableScope.FrameCount);
        Assert.NotNull(ctx.VariableStore);
    }

    /// <summary>
    /// Dispose is idempotent on a context that owns its VariableStore —
    /// the underlying Arena.Dispose is itself idempotent, but the
    /// _disposeCount guard belt-and-suspenders this so a stray second
    /// Dispose never reaches the arena at all.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        ExecutionContext ctx = new Heliosoph.DatumV.Execution.ExecutionContext(CreateCatalog());
        ctx.Dispose();
        ctx.Dispose();
        // Reaching here without throwing is the assertion.
    }

    /// <summary>
    /// A caller-supplied VariableStore is borrowed: the context's Dispose
    /// leaves it untouched (no AddReference/Release, no Dispose call) so
    /// the caller keeps using it afterwards. This is the shape Derive
    /// relies on to share its parent's substrate without tearing it down
    /// when the child context disposes.
    /// </summary>
    [Fact]
    public void BorrowedVariableStore_DisposeDoesNotReleaseIt()
    {
        Arena caller = CreateArena();

        ExecutionContext ctx = new Heliosoph.DatumV.Execution.ExecutionContext(
            CreateCatalog(),
            variableStore: caller);

        Assert.Same(caller, ctx.VariableStore);
        ctx.Dispose();

        // Caller's arena is still usable — disposing the context with a
        // borrowed arena does not touch the arena's lifecycle.
        caller.StoreString("still alive");
    }

    /// <summary>
    /// The crux of the lift-on-bind contract: a DataValue whose payload
    /// lives in a producing query's arena gets copied into a managed
    /// ValueRef on Declare, so the binding remains readable after the
    /// producing arena is gone. Without the lift, the bound value's
    /// offsets would point into a freed arena.
    /// </summary>
    [Fact]
    public void Declare_LiftsArenaPayloadToManagedString()
    {
        const string longText = "This string is more than 16 UTF-8 bytes, so the payload lives in an arena rather than inline.";

        Arena producingArena = CreateArena();
        producingArena.AddReference();
        DataValue produced = DataValue.FromString(longText, producingArena);

        using ExecutionContext ctx = CreateExecutionContext();
        ctx.Declare("greeting", produced, producingArena);

        // Drop the producing arena — the binding must survive the source's
        // recycle.
        producingArena.ReleaseReference();

        ValueRef bound = ctx.VariableScope.Get("greeting");
        Assert.Equal(longText, bound.AsString());
    }

    /// <summary>
    /// Set has the same lift contract as Declare — overwriting a binding
    /// with a value from an external arena must stabilise the new payload
    /// into VariableStore. Verifies the second value reads back correctly
    /// against the same producing arena.
    /// </summary>
    [Fact]
    public void Set_LiftsNewValueToManagedString()
    {
        const string firstText = "First long enough payload to require an arena copy on bind.";
        const string secondText = "Second long enough payload that overwrites the first via SET.";

        Arena producingArena = CreateArena();
        producingArena.AddReference();

        using ExecutionContext ctx = CreateExecutionContext();
        ctx.Declare("msg", DataValue.FromString(firstText, producingArena), producingArena);
        ctx.Set("msg", DataValue.FromString(secondText, producingArena), producingArena);

        ValueRef current = ctx.VariableScope.Get("msg");
        Assert.Equal(secondText, current.AsString());
    }

    /// <summary>
    /// Inline values (≤16 UTF-8 bytes for strings, all numerics) carry
    /// their payload inside the DataValue struct — the lift is a no-op.
    /// Sanity-check that Declare round-trips an Int32 correctly to
    /// confirm the path doesn't accidentally try to stabilise an inline
    /// payload.
    /// </summary>
    [Fact]
    public void Declare_InlineValueBinding_RoundTripsThroughVariableScope()
    {
        Arena producing = CreateArena();
        producing.AddReference();

        using ExecutionContext ctx = CreateExecutionContext();
        ctx.Declare("count", DataValue.FromInt32(42), producing);

        ValueRef val = ctx.VariableScope.Get("count");
        Assert.Equal(42, val.AsInt32());
    }
}
