using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Contexts;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Phase-A3: <see cref="FunctionRegistry.IsVisibleInContext"/> resolves
/// according to the per-function <c>Contexts</c> membership and the
/// context's <c>Borrows</c> opt-ins.
/// </summary>
public sealed class FunctionContextVisibilityTests
{
    private static FunctionRegistry MakeRegistry()
    {
        FunctionRegistry registry = new();
        registry.RegisterScalar<GlobalFn>();
        registry.RegisterScalar<AnimationOnlyFn>();
        return registry;
    }

    private static FunctionContextRegistry MakeContexts()
    {
        FunctionContextRegistry contexts = new();
        contexts.Register<PureContext>();
        contexts.Register<TestAnimationContext>();
        contexts.Register<TestExtendedContext>();
        return contexts;
    }

    [Fact]
    public void Global_VisibleAtTopLevel()
    {
        FunctionRegistry registry = MakeRegistry();
        Assert.True(registry.IsVisibleInContext(
            new QualifiedName("system", "global_fn"),
            currentContextName: null,
            contexts: null));
    }

    [Fact]
    public void Global_VisibleInsideAnyContext()
    {
        FunctionRegistry registry = MakeRegistry();
        FunctionContextRegistry contexts = MakeContexts();
        Assert.True(registry.IsVisibleInContext(
            new QualifiedName("system", "global_fn"),
            currentContextName: "animation",
            contexts));
    }

    [Fact]
    public void ContextRestricted_HiddenAtTopLevel()
    {
        FunctionRegistry registry = MakeRegistry();
        Assert.False(registry.IsVisibleInContext(
            new QualifiedName("system", "animation_only_fn"),
            currentContextName: null,
            contexts: null));
    }

    [Fact]
    public void ContextRestricted_VisibleInsideDeclaredContext()
    {
        FunctionRegistry registry = MakeRegistry();
        FunctionContextRegistry contexts = MakeContexts();
        Assert.True(registry.IsVisibleInContext(
            new QualifiedName("system", "animation_only_fn"),
            currentContextName: "animation",
            contexts));
    }

    [Fact]
    public void ContextRestricted_VisibleInsideDescendantContext()
    {
        // TestExtendedContext extends TestAnimationContext, so a function
        // restricted to "animation" should be visible inside "extended" too.
        FunctionRegistry registry = MakeRegistry();
        FunctionContextRegistry contexts = MakeContexts();
        Assert.True(registry.IsVisibleInContext(
            new QualifiedName("system", "animation_only_fn"),
            currentContextName: "extended",
            contexts));
    }

    [Fact]
    public void ContextRestricted_HiddenInsideUnrelatedContext()
    {
        FunctionRegistry registry = MakeRegistry();
        FunctionContextRegistry contexts = MakeContexts();
        Assert.False(registry.IsVisibleInContext(
            new QualifiedName("system", "animation_only_fn"),
            currentContextName: "pure",  // pure has no parent → walk yields only self
            contexts));
    }

    [Fact]
    public void Borrow_MakesGlobalFunctionVisibleInContextThatDoesntInherit()
    {
        // BorrowingContext explicitly borrows the animation-restricted function
        // by name, even though it doesn't have AnimationContext in its parent
        // chain. The borrow makes the function visible inside borrowing context.
        FunctionRegistry registry = MakeRegistry();
        FunctionContextRegistry contexts = MakeContexts();
        contexts.Register<BorrowingContext>();
        Assert.True(registry.IsVisibleInContext(
            new QualifiedName("system", "animation_only_fn"),
            currentContextName: "borrowing",
            contexts));
    }

    [Fact]
    public void UnregisteredFunction_NotVisibleAnywhere()
    {
        FunctionRegistry registry = MakeRegistry();
        Assert.False(registry.IsVisibleInContext(
            new QualifiedName("system", "no_such_fn"),
            currentContextName: null,
            contexts: null));
    }

    // ----- helper function + context types -----

    private sealed class GlobalFn : IFunction, IScalarFunction
    {
        public static string Name => "global_fn";
        public static FunctionCategory Category => FunctionCategory.Numeric;
        public static string Description => "";
        public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
            [new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.Constant(DataKind.Int32))];
        public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) => DataKind.Int32;
        public ValueTask<ValueRef> ExecuteAsync(
            ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken ct) =>
            new(ValueRef.FromInt32(0));
    }

    private sealed class AnimationOnlyFn : IFunction, IScalarFunction
    {
        public static string Name => "animation_only_fn";
        public static FunctionCategory Category => FunctionCategory.Numeric;
        public static string Description => "";
        public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
            [new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.Constant(DataKind.Int32))];
        public static IReadOnlyList<string> Contexts => [TestAnimationContext.Name];
        public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) => DataKind.Int32;
        public ValueTask<ValueRef> ExecuteAsync(
            ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken ct) =>
            new(ValueRef.FromInt32(0));
    }

    private sealed class TestAnimationContext : IFunctionContext
    {
        public static string Name => "animation";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } =
            [new LambdaParameterSpec("t", DataKind.Float32)];
        public static string? ParentName => "pure";
    }

    private sealed class TestExtendedContext : IFunctionContext
    {
        public static string Name => "extended";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];
        public static string? ParentName => "animation";
    }

    private sealed class BorrowingContext : IFunctionContext
    {
        public static string Name => "borrowing";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } = [];
        public static string? ParentName => "pure";
        public static IReadOnlyList<string> Borrows { get; } = ["animation_only_fn"];
    }
}
