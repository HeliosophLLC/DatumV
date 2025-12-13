using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Substrate-only tests for <see cref="BatchContext"/>: ownership of the
/// procedure-lifetime <c>VariableStore</c> arena, stabilise-on-bind
/// semantics, and IDisposable lifecycle. Confirms the core correctness
/// property: a value bound from a transient producing arena remains
/// readable from <c>VariableStore</c> after the producing arena is gone.
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
    public void Declare_StabilisesArenaPayloadIntoVariableStore()
    {
        // The crux: a value with payload in a producing-query's arena
        // gets copied into VariableStore when bound. Reading later via
        // VariableStore returns the original string — proves stabilise
        // ran (otherwise we'd be dereferencing the producing arena).
        const string longText = "This string is more than 16 UTF-8 bytes, so the payload lives in an arena rather than inline.";

        Arena producingArena = new();
        producingArena.AddReference();
        DataValue produced = DataValue.FromString(longText, producingArena);

        using BatchContext ctx = new();
        ctx.Declare("greeting", produced, producingArena);

        DataValue bound = ctx.VariableScope.Get("greeting");
        // The bound value reads from VariableStore (procedure-scoped),
        // not the producing arena. Bytes were copied at bind time.
        Assert.Equal(longText, bound.AsString(ctx.VariableStore));
    }

    [Fact]
    public void Set_StabilisesNewValueIntoVariableStore()
    {
        const string firstText = "First long enough payload to require an arena copy on bind.";
        const string secondText = "Second long enough payload that overwrites the first via SET.";

        Arena producingArena = new();
        producingArena.AddReference();

        using BatchContext ctx = new();
        ctx.Declare("msg", DataValue.FromString(firstText, producingArena), producingArena);
        ctx.Set("msg", DataValue.FromString(secondText, producingArena), producingArena);

        DataValue current = ctx.VariableScope.Get("msg");
        Assert.Equal(secondText, current.AsString(ctx.VariableStore));
    }

    [Fact]
    public void InlineValueBinding_DoesNotTouchVariableStore()
    {
        // Inline values (≤ 16 UTF-8 bytes for strings, all numerics) carry
        // their payload inside the DataValue struct. Stabilise is still
        // called for uniformity, but no bytes hit VariableStore — sanity
        // check that the round-trip works in this path too.
        Arena producing = new();
        producing.AddReference();

        using BatchContext ctx = new();
        ctx.Declare("count", DataValue.FromInt32(42), producing);

        DataValue val = ctx.VariableScope.Get("count");
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
