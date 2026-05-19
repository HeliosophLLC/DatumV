using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// Phase-A5: <see cref="LambdaMatcher"/> matches Lambda-kind values and
/// carries signature metadata; <see cref="LambdaSignatureValidator"/>
/// performs plan-time structural checks against the canonical parameter
/// list declared by the context.
/// </summary>
public sealed class LambdaMatcherTests
{
    private static LambdaExpression ParseLambda(string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call = (FunctionCallExpression)select.Statement.Columns[0].Expression;
        return (LambdaExpression)call.Arguments[1];
    }

    // ----- matcher -----

    [Fact]
    public void Matcher_AcceptsLambdaKind()
    {
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Image));
        Assert.True(matcher.Matches(DataKind.Lambda));
    }

    [Fact]
    public void Matcher_RejectsOtherKinds()
    {
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Image));
        Assert.False(matcher.Matches(DataKind.Int32));
        Assert.False(matcher.Matches(DataKind.Image));
    }

    [Fact]
    public void Matcher_CarriesContextAndReturns()
    {
        DataKindMatcher returns = DataKindMatcher.Exact(DataKind.Float32);
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation", returns);
        Assert.Equal("animation", matcher.ContextName);
        Assert.Same(returns, matcher.Returns);
    }

    [Fact]
    public void Matcher_NullReturns_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => DataKindMatcher.Lambda("animation", returns: null!));
    }

    [Fact]
    public void Matcher_UnscopedLambda_AcceptsNullContext()
    {
        LambdaMatcher matcher = DataKindMatcher.Lambda(null,
            DataKindMatcher.Exact(DataKind.Int32));
        Assert.Null(matcher.ContextName);
        Assert.Contains("<unscoped>", matcher.Describe());
    }

    // ----- signature validator -----

    [Fact]
    public void Validator_MatchingParameterCount_DoesNotThrow()
    {
        FunctionContextRegistry contexts = new();
        contexts.Register<TestAnimationContext>();
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Float32));
        LambdaExpression lambda = ParseLambda("t -> t * 2");

        LambdaSignatureValidator.Validate("animate_gif", "render_frame", lambda, matcher, contexts);
        // no throw = pass
    }

    [Fact]
    public void Validator_RenamedParameter_StillAccepted()
    {
        // Context declares parameter name "t", but user can rename to "u".
        FunctionContextRegistry contexts = new();
        contexts.Register<TestAnimationContext>();
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Float32));
        LambdaExpression lambda = ParseLambda("u -> u * 2");

        LambdaSignatureValidator.Validate("animate_gif", "render_frame", lambda, matcher, contexts);
        // no throw — names are advisory, not pinned
    }

    [Fact]
    public void Validator_WrongParameterCount_Throws()
    {
        FunctionContextRegistry contexts = new();
        contexts.Register<TestAnimationContext>();
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Float32));
        LambdaExpression lambda = ParseLambda("(a, b) -> a + b");  // 2 params, context wants 1

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => LambdaSignatureValidator.Validate(
                "animate_gif", "render_frame", lambda, matcher, contexts));
        Assert.Contains("1 argument(s)", ex.Message);
        Assert.Contains("2 parameter(s)", ex.Message);
    }

    [Fact]
    public void Validator_UnregisteredContext_Throws()
    {
        FunctionContextRegistry contexts = new();  // empty
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Float32));
        LambdaExpression lambda = ParseLambda("t -> t");

        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => LambdaSignatureValidator.Validate(
                "animate_gif", "render_frame", lambda, matcher, contexts));
        Assert.Contains("no such context is registered", ex.Message);
    }

    [Fact]
    public void Validator_NullContextRegistry_SkipsCount()
    {
        LambdaMatcher matcher = DataKindMatcher.Lambda("animation",
            DataKindMatcher.Exact(DataKind.Float32));
        LambdaExpression lambda = ParseLambda("(a, b, c) -> a + b + c");

        // No throw: registry is null, so the count check is skipped (we have
        // no source of truth for the expected count).
        LambdaSignatureValidator.Validate(
            "animate_gif", "render_frame", lambda, matcher, contexts: null);
    }

    [Fact]
    public void Validator_UnscopedLambda_NoChecks()
    {
        FunctionContextRegistry contexts = new();
        contexts.Register<PureContext>();
        LambdaMatcher matcher = DataKindMatcher.Lambda(contextName: null,
            DataKindMatcher.Exact(DataKind.Int32));
        LambdaExpression lambda = ParseLambda("(a, b, c) -> a + b + c");

        // No throw: matcher.ContextName is null so the validator skips
        // structural checks entirely.
        LambdaSignatureValidator.Validate(
            "consumer", "callback", lambda, matcher, contexts);
    }

    // ----- helper context types -----

    private sealed class TestAnimationContext : IFunctionContext
    {
        public static string Name => "animation";
        public static IReadOnlyList<LambdaParameterSpec> Parameters { get; } =
            [new LambdaParameterSpec("t", DataKind.Float32)];
        public static string? ParentName => "pure";
    }
}
