using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Substrate-only tests for <see cref="VariableScope"/>: push/pop, declare,
/// set, lookup, scope-walk semantics. Uses inline DataValue forms
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
        DataValue val = DataValue.FromBoolean(true);
        scope.Declare("flag", val);
        Assert.True(scope.TryGet("flag", out DataValue retrieved));
        Assert.Equal(val, retrieved);
    }

    [Fact]
    public void Declare_TwiceInSameFrame_Throws()
    {
        VariableScope scope = new();
        scope.Declare("x", DataValue.FromInt32(1));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Declare("x", DataValue.FromInt32(2)));
        Assert.Contains("already declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declare_InOuter_VisibleFromInner()
    {
        VariableScope scope = new();
        scope.Declare("x", DataValue.FromInt32(42));
        scope.PushFrame();
        Assert.True(scope.TryGet("x", out DataValue val));
        Assert.Equal(42, val.AsInt32());
    }

    [Fact]
    public void Declare_InInner_ShadowsOuter()
    {
        // Outer x = 1; inner x = 2 — inner shadows outer for the lifetime
        // of the inner block. Both bindings exist; the lookup returns the
        // inner one because TryGet walks innermost-first.
        VariableScope scope = new();
        scope.Declare("x", DataValue.FromInt32(1));
        scope.PushFrame();
        scope.Declare("x", DataValue.FromInt32(2));

        Assert.True(scope.TryGet("x", out DataValue inner));
        Assert.Equal(2, inner.AsInt32());

        scope.PopFrame();
        Assert.True(scope.TryGet("x", out DataValue outer));
        Assert.Equal(1, outer.AsInt32());
    }

    [Fact]
    public void Set_WalksOutwardToFindBinding()
    {
        // Variable declared in outer; SET from inner should mutate the
        // outer binding, not create a new one in the inner frame.
        VariableScope scope = new();
        scope.Declare("counter", DataValue.FromInt32(0));
        scope.PushFrame();
        scope.Set("counter", DataValue.FromInt32(5));
        scope.PopFrame();

        Assert.True(scope.TryGet("counter", out DataValue val));
        Assert.Equal(5, val.AsInt32());
    }

    [Fact]
    public void Set_UndeclaredName_Throws()
    {
        VariableScope scope = new();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => scope.Set("never_declared", DataValue.FromInt32(1)));
        Assert.Contains("not declared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGet_UndeclaredName_ReturnsFalse()
    {
        VariableScope scope = new();
        Assert.False(scope.TryGet("missing", out DataValue _));
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
        scope.Declare("Counter", DataValue.FromInt32(7));

        Assert.True(scope.TryGet("counter", out DataValue lower));
        Assert.True(scope.TryGet("COUNTER", out DataValue upper));
        Assert.Equal(7, lower.AsInt32());
        Assert.Equal(7, upper.AsInt32());
    }

    [Fact]
    public void Pop_RemovesInnerBindings()
    {
        VariableScope scope = new();
        scope.PushFrame();
        scope.Declare("temp", DataValue.FromInt32(123));
        scope.PopFrame();
        Assert.False(scope.TryGet("temp", out DataValue _));
    }
}
