using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution.Contexts;

/// <summary>
/// Phase-A2 substrate: registration, lookup, and ancestor-walk behaviours
/// for <see cref="FunctionContextRegistry"/>.
/// </summary>
public sealed class FunctionContextRegistryTests
{
    [Fact]
    public void Register_StoresContextDescriptor()
    {
        FunctionContextRegistry registry = new();
        registry.Register<PureContext>();

        FunctionContextDescriptor? descriptor = registry.TryGet("pure");
        Assert.NotNull(descriptor);
        Assert.Equal("pure", descriptor.Name);
        Assert.Null(descriptor.ParentName);
        Assert.Empty(descriptor.Borrows);
        Assert.Empty(descriptor.Parameters);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        FunctionContextRegistry registry = new();
        Assert.Null(registry.TryGet("missing"));
    }

    [Fact]
    public void Get_UnknownName_Throws()
    {
        FunctionContextRegistry registry = new();
        ArgumentException ex = Assert.Throws<ArgumentException>(() => registry.Get("missing"));
        Assert.Contains("'missing'", ex.Message);
    }

    [Fact]
    public void Register_Idempotent_LastWriteWins()
    {
        FunctionContextRegistry registry = new();
        registry.Register<PureContext>();
        registry.Register<PureContext>();   // re-register
        Assert.Single(registry.Names);
    }

    [Fact]
    public void CreateDefault_RegistersPureContext()
    {
        FunctionContextRegistry registry = FunctionContextRegistry.CreateDefault();
        Assert.Contains("pure", registry.Names);
    }

    [Fact]
    public void WalkAncestors_Linear_VisitsSelfThenParents()
    {
        FunctionContextRegistry registry = new();
        registry.Register<PureContext>();
        registry.Register<TestChildContext>();
        registry.Register<TestGrandchildContext>();

        List<string> visited = registry
            .WalkAncestors(TestGrandchildContext.Name)
            .Select(d => d.Name)
            .ToList();

        Assert.Equal(["grandchild", "child", "pure"], visited);
    }

    [Fact]
    public void WalkAncestors_UnknownContext_YieldsEmpty()
    {
        FunctionContextRegistry registry = new();
        Assert.Empty(registry.WalkAncestors("missing"));
    }

    [Fact]
    public void WalkAncestors_SelfReferential_TerminatesCleanly()
    {
        // Defensive: a malformed parent chain (cycle) shouldn't hang.
        FunctionContextRegistry registry = new();
        registry.Register<SelfReferentialContext>();

        List<FunctionContextDescriptor> visited = registry
            .WalkAncestors("self")
            .ToList();

        Assert.Single(visited);
        Assert.Equal("self", visited[0].Name);
    }

    [Fact]
    public void LambdaParameterSpec_CarriesNameAndKind()
    {
        LambdaParameterSpec spec = new("t", DataKind.Float32);
        Assert.Equal("t", spec.Name);
        Assert.Equal(DataKind.Float32, spec.Kind);
    }

    // ----- helper context types -----

    private sealed class TestChildContext : IFunctionContext
    {
        public static string Name => "child";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];
        public static string? ParentName => "pure";
    }

    private sealed class TestGrandchildContext : IFunctionContext
    {
        public static string Name => "grandchild";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];
        public static string? ParentName => "child";
    }

    private sealed class SelfReferentialContext : IFunctionContext
    {
        public static string Name => "self";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];
        public static string? ParentName => "self"; // cycle
    }
}
