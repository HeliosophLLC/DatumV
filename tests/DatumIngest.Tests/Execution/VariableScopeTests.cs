using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Substrate-only tests for <see cref="VariableScope"/>: push/pop, declare,
/// set, lookup, scope-walk semantics. Uses inline ValueRef forms
/// (bool, int) so no arena interaction is required.
/// </summary>
public sealed class VariableScopeTests : ServiceTestBase
{
    [Fact]
    public void NewScope_HasOneRootFrame()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        Assert.Equal(1, scope.FrameCount);
    }

    [Fact]
    public void PushFrame_IncreasesFrameCount()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.PushFrame();
        Assert.Equal(2, scope.FrameCount);
        scope.PushFrame();
        Assert.Equal(3, scope.FrameCount);
    }

    [Fact]
    public void PopFrame_DecreasesFrameCount()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.PushFrame();
        scope.PushFrame();
        scope.PopFrame();
        Assert.Equal(2, scope.FrameCount);
    }

    [Fact]
    public void PopRootFrame_Throws()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scope.PopFrame());
        Assert.Contains("root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declare_BindsInTopFrame_LookupReturnsValue()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        ValueRef val = ValueRef.FromBoolean(true);
        scope.Declare("flag", val);
        Assert.True(scope.TryGet("flag", out ValueRef retrieved));
        Assert.True(retrieved.AsBoolean());
    }

    [Fact]
    public void Declare_TwiceInSameFrame_Throws()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.Declare("x", ValueRef.FromInt32(1));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Declare("x", ValueRef.FromInt32(2)));
        Assert.Contains("already declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declare_InOuter_VisibleFromInner()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
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
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
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
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
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
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Set("never_declared", ValueRef.FromInt32(1)));
        Assert.Contains("not declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGet_UndeclaredName_ReturnsFalse()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        Assert.False(scope.TryGet("missing", out ValueRef _));
    }

    [Fact]
    public void Get_UndeclaredName_Throws()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Get("missing"));
        Assert.Contains("not declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NameLookup_IsCaseInsensitive()
    {
        // X and x resolve to the same binding — matches how the codebase
        // handles UDF names and SQL identifiers generally.
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.Declare("Counter", ValueRef.FromInt32(7));

        Assert.True(scope.TryGet("counter", out ValueRef lower));
        Assert.True(scope.TryGet("COUNTER", out ValueRef upper));
        Assert.Equal(7, lower.AsInt32());
        Assert.Equal(7, upper.AsInt32());
    }

    [Fact]
    public void Pop_RemovesInnerBindings()
    {
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
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

        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));
        Assert.True(scope.TryGet("tensor", out ValueRef retrieved));
        Assert.True(retrieved.IsArray);
        Assert.Same(tensor, retrieved.Materialized);
    }

    [Fact]
    public void Declare_LargeManagedPayload_NotifiesAccountant()
    {
        // 1M Float32 elements = 4 MB managed payload bound to a variable.
        // Declare must push that byte count into the accountant so the
        // plan-wide budget sees it.
        float[] tensor = new float[1_000_000];
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        Assert.Equal(0, accountant.CurrentResidentBytes);
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));
        Assert.Equal(tensor.Length * 4L, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void Declare_InlineValue_DoesNotChangeResidency()
    {
        // Inline carriers (int, bool, float scalar) have zero managed payload.
        // Their declare must not move the accountant counter.
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        scope.Declare("flag", ValueRef.FromBoolean(true));
        scope.Declare("count", ValueRef.FromInt32(42));
        scope.Declare("ratio", ValueRef.FromFloat32(1.5f));

        Assert.Equal(0, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void PopFrame_ReleasesAllBindingsBytes()
    {
        // Declares in a pushed frame, pops the frame, accountant returns to baseline.
        float[] tensor = new float[100_000];
        byte[] blob = new byte[8_192];
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        scope.PushFrame();
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));
        scope.Declare("blob", ValueRef.FromBytes(DataKind.UInt8, blob, isArray: true));
        long peak = accountant.CurrentResidentBytes;
        Assert.Equal(tensor.Length * 4L + blob.Length, peak);

        scope.PopFrame();
        Assert.Equal(0, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void Set_ReplacingPayload_AdjustsAccountantByDelta()
    {
        // Declare with a small payload, SET to a larger one — delta is the
        // difference, not the absolute size of either operand.
        float[] small = new float[1_000];
        float[] large = new float[10_000];
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        scope.Declare("x", ValueRef.FromPrimitiveArray(small, DataKind.Float32));
        long afterDeclare = accountant.CurrentResidentBytes;
        Assert.Equal(small.Length * 4L, afterDeclare);

        scope.Set("x", ValueRef.FromPrimitiveArray(large, DataKind.Float32));
        Assert.Equal(large.Length * 4L, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void Set_ReplacingLargerWithSmaller_ReleasesDelta()
    {
        float[] large = new float[10_000];
        float[] small = new float[1_000];
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        scope.Declare("x", ValueRef.FromPrimitiveArray(large, DataKind.Float32));
        scope.Set("x", ValueRef.FromPrimitiveArray(small, DataKind.Float32));

        Assert.Equal(small.Length * 4L, accountant.CurrentResidentBytes);
    }

    [Fact]
    public void DeclareInLoop_PopInLoop_AccountantStaysBounded()
    {
        // Models the "11 MB tensor per FOR iteration" pattern from the
        // VariableScope docstring: each iteration declares the tensor,
        // pops the frame, accountant returns to zero. Verifies the
        // declare-pop cycle doesn't accumulate.
        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);

        for (int i = 0; i < 50; i++)
        {
            float[] tensor = new float[100_000];  // ~400 KB
            scope.PushFrame();
            scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));
            Assert.Equal(tensor.Length * 4L, accountant.CurrentResidentBytes);
            scope.PopFrame();
            Assert.Equal(0, accountant.CurrentResidentBytes);
        }
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

        using MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        scope.Declare("tensor", ValueRef.FromPrimitiveArray(tensor, DataKind.Float32));

        Arena store = CreateArena();
        store.AddReference();
        long bytesBeforeRead = store.BytesWritten;

        using Heliosoph.DatumV.Execution.ExecutionContext context = CreateExecutionContext(store: store, accountant: accountant);
        Heliosoph.DatumV.Execution.ExecutionContext scoped = context.Derive(variableScope: scope, variableStore: store);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();

        Heliosoph.DatumV.Parsing.Ast.ColumnReference ref_ = new(
            ColumnName: "tensor",
            TableName: null,
            Span: null);
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty);

        ValueRef result = await evaluator.EvaluateAsValueRefAsync(ref_, frame);

        Assert.True(result.IsArray);
        Assert.Same(tensor, result.Materialized);
        Assert.Equal(bytesBeforeRead, store.BytesWritten);
    }
}
