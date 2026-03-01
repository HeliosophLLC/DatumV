using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Substrate-only tests for <see cref="BatchContext"/>: ownership of the
/// procedure-lifetime <c>VariableStore</c> arena, lift-on-bind semantics
/// (DataValue → managed-payload ValueRef), and IDisposable lifecycle.
/// Confirms the core correctness property: a value bound from a transient
/// producing arena remains readable after the producing arena is gone.
/// </summary>
public sealed class BatchContextTests
{
    [Fact]
    public void NewBatchContext_HasFreshStoreAndScope()
    {
        using BatchContext ctx = new();
        Assert.NotNull(ctx.VariableStore);
        Assert.NotNull(ctx.VariableScope);
        Assert.Equal(1, ctx.VariableScope.FrameCount);
    }

    [Fact]
    public void Declare_LiftsArenaPayloadToManagedString()
    {
        // The crux: a value with payload in a producing-query's arena
        // gets lifted into a managed-payload ValueRef when bound. Reading
        // later returns the original string — proves the lift ran
        // (otherwise we'd be dereferencing the producing arena, which is
        // disposed below).
        const string longText = "This string is more than 16 UTF-8 bytes, so the payload lives in an arena rather than inline.";

        Arena producingArena = new();
        producingArena.AddReference();
        DataValue produced = DataValue.FromString(longText, producingArena);

        using BatchContext ctx = new();
        ctx.Declare("greeting", produced, producingArena);

        // Drop the producing arena's reference — the binding must survive.
        producingArena.ReleaseReference();

        ValueRef bound = ctx.VariableScope.Get("greeting");
        Assert.Equal(longText, bound.AsString());
    }

    [Fact]
    public void Set_LiftsNewValueToManagedString()
    {
        const string firstText = "First long enough payload to require an arena copy on bind.";
        const string secondText = "Second long enough payload that overwrites the first via SET.";

        Arena producingArena = new();
        producingArena.AddReference();

        using BatchContext ctx = new();
        ctx.Declare("msg", DataValue.FromString(firstText, producingArena), producingArena);
        ctx.Set("msg", DataValue.FromString(secondText, producingArena), producingArena);

        ValueRef current = ctx.VariableScope.Get("msg");
        Assert.Equal(secondText, current.AsString());
    }

    [Fact]
    public void InlineValueBinding_DoesNotTouchVariableStore()
    {
        // Inline values (≤ 16 UTF-8 bytes for strings, all numerics) carry
        // their payload inside the DataValue struct. The lift is a no-op —
        // sanity check that the round-trip works in this path too.
        Arena producing = new();
        producing.AddReference();

        using BatchContext ctx = new();
        ctx.Declare("count", DataValue.FromInt32(42), producing);

        ValueRef val = ctx.VariableScope.Get("count");
        Assert.Equal(42, val.AsInt32());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        BatchContext ctx = new();
        ctx.Dispose();
        ctx.Dispose();
        // Reaching here without throwing is the whole assertion; double-
        // dispose must never double-release the arena's baseline ref.
    }

    [Fact]
    public void Dispose_ReleasesBaselineReference()
    {
        BatchContext ctx = new();
        int beforeDispose = ctx.VariableStore.ReferenceCount;
        ctx.Dispose();
        int afterDispose = ctx.VariableStore.ReferenceCount;

        Assert.Equal(1, beforeDispose);   // baseline ref held by ctx
        Assert.Equal(0, afterDispose);    // baseline released on dispose
    }
}
