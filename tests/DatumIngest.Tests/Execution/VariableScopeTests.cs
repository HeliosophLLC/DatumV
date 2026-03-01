using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Substrate-only tests for <see cref="VariableScope"/>: push/pop, declare,
/// set, lookup, scope-walk semantics. Uses inline ValueRef forms
/// (bool, int) so no arena interaction is required — payload-store
/// semantics are covered separately in <see cref="BatchContextTests"/>.
/// </summary>
public sealed class VariableScopeTests
{
    [Fact]
    public void NewScope_HasOneRootFrame()
    {
        VariableScope scope = new();
        Assert.Equal(1, scope.FrameCount);
    }

    [Fact]
    public void PushFrame_IncreasesFrameCount()
    {
        VariableScope scope = new();
        scope.PushFrame();
        Assert.Equal(2, scope.FrameCount);
        scope.PushFrame();
        Assert.Equal(3, scope.FrameCount);
    }

    [Fact]
    public void PopFrame_DecreasesFrameCount()
    {
        VariableScope scope = new();
        scope.PushFrame();
        scope.PushFrame();
        scope.PopFrame();
        Assert.Equal(2, scope.FrameCount);
    }

    [Fact]
    public void PopRootFrame_Throws()
    {
        VariableScope scope = new();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scope.PopFrame());
        Assert.Contains("root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declare_BindsInTopFrame_LookupReturnsValue()
    {
        VariableScope scope = new();
        ValueRef val = ValueRef.FromBoolean(true);
        scope.Declare("flag", val);
        Assert.True(scope.TryGet("flag", out ValueRef retrieved));
        Assert.True(retrieved.AsBoolean());
    }

    [Fact]
    public void Declare_TwiceInSameFrame_Throws()
    {
        VariableScope scope = new();
        scope.Declare("x", ValueRef.FromInt32(1));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Declare("x", ValueRef.FromInt32(2)));
        Assert.Contains("already declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declare_InOuter_VisibleFromInner()
    {
        VariableScope scope = new();
        scope.Declare("x", ValueRef.FromInt32(42));
        scope.PushFrame();
        Assert.True(scope.TryGet("x", out ValueRef val));
        Assert.Equal(42, val.AsInt32());
    }

    [Fact]
    public void Declare_InInner_ShadowsOuter()
    {
        // Outer x = 1; inner x = 2 — inner shadows outer for the lifetime
        // of the inner block. Both bindings exist; the lookup returns the
        // inner one because TryGet walks innermost-first.
        VariableScope scope = new();
        scope.Declare("x", ValueRef.FromInt32(1));
        scope.PushFrame();
        scope.Declare("x", ValueRef.FromInt32(2));

        Assert.True(scope.TryGet("x", out ValueRef inner));
        Assert.Equal(2, inner.AsInt32());

        scope.PopFrame();
        Assert.True(scope.TryGet("x", out ValueRef outer));
        Assert.Equal(1, outer.AsInt32());
    }

    [Fact]
    public void Set_WalksOutwardToFindBinding()
    {
        // Variable declared in outer; SET from inner should mutate the
        // outer binding, not create a new one in the inner frame.
        VariableScope scope = new();
        scope.Declare("counter", ValueRef.FromInt32(0));
        scope.PushFrame();
        scope.Set("counter", ValueRef.FromInt32(5));
        scope.PopFrame();

        Assert.True(scope.TryGet("counter", out ValueRef val));
        Assert.Equal(5, val.AsInt32());
    }

    [Fact]
    public void Set_UndeclaredName_Throws()
    {
        VariableScope scope = new();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Set("never_declared", ValueRef.FromInt32(1)));
        Assert.Contains("not declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGet_UndeclaredName_ReturnsFalse()
    {
        VariableScope scope = new();
        Assert.False(scope.TryGet("missing", out ValueRef _));
    }

    [Fact]
    public void Get_UndeclaredName_Throws()
    {
        VariableScope scope = new();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Get("missing"));
        Assert.Contains("not declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NameLookup_IsCaseInsensitive()
    {
        // X and x resolve to the same binding — matches how the codebase
        // handles UDF names and SQL identifiers generally.
        VariableScope scope = new();
        scope.Declare("Counter", ValueRef.FromInt32(7));

        Assert.True(scope.TryGet("counter", out ValueRef lower));
        Assert.True(scope.TryGet("COUNTER", out ValueRef upper));
        Assert.Equal(7, lower.AsInt32());
        Assert.Equal(7, upper.AsInt32());
    }

    [Fact]
    public void Pop_RemovesInnerBindings()
    {
        VariableScope scope = new();
        scope.PushFrame();
        scope.Declare("temp", ValueRef.FromInt32(123));
        scope.PopFrame();
        Assert.False(scope.TryGet("temp", out ValueRef _));
    }

    [Fact]
    public void Declare_ManagedPayload_RoundTripsWithoutArena()
    {
        // The whole point of holding ValueRef in scope: a large managed
        // payload (a Float32 tensor) stays as a managed array reference
        // across DECLARE / TryGet, no arena involved.
        float[] tensor = new float[1024];
        for (int i = 0; i < tensor.Length; i++) tensor[i] = i * 0.5f;

        VariableScope scope = new();
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));
        Assert.True(scope.TryGet("tensor", out ValueRef retrieved));
        Assert.True(retrieved.IsArray);
        Assert.Same(tensor, retrieved.Materialized);
    }

    [Fact]
    public async Task EvaluateAsValueRefAsync_ReadsManagedPayloadDirectly_NoArenaWrite()
    {
        // The architectural probe: ExpressionEvaluator.EvaluateAsValueRefAsync
        // for a ColumnReference that resolves to a variable must return the
        // stored managed payload by reference. Arena BytesWritten must NOT
        // grow — proves the read short-circuits through the ValueRef fast
        // path instead of materialising into frame.Target.
        float[] tensor = new float[1_000_000];
        for (int i = 0; i < tensor.Length; i++) tensor[i] = i * 0.001f;

        VariableScope scope = new();
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));

        Arena store = new();
        store.AddReference();
        long bytesBeforeRead = store.BytesWritten;

        ExpressionEvaluator evaluator = new(
            DatumIngest.Functions.FunctionRegistry.CreateDefault(),
            store: store,
            variableScope: scope,
            variableStore: store);

        DatumIngest.Parsing.Ast.ColumnReference ref_ = new(
            ColumnName: "tensor",
            TableName: null,
            Span: null);
        EvaluationFrame frame = new(Row.Empty, store, store);

        ValueRef result = await evaluator.EvaluateAsValueRefAsync(ref_, frame);

        Assert.True(result.IsArray);
        Assert.Same(tensor, result.Materialized);
        Assert.Equal(bytesBeforeRead, store.BytesWritten);
    }
}
