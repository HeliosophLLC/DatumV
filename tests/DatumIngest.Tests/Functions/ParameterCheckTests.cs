using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Direct unit tests against the <see cref="ParameterCheck"/> hierarchy's
/// <c>Validate</c> method — one fixture per subclass covering NULL pass-through,
/// in-range pass, out-of-range failure with a recognisable message, and
/// non-numeric kind pass-through (the type-system rejects mismatched kinds
/// before the check runs, so check enforcement is decoupled from kind checks).
/// </summary>
public sealed class ParameterCheckTests
{
    [Fact]
    public void BetweenCheck_InRange_ReturnsNull()
    {
        BetweenCheck c = new(0m, 1m);
        Assert.Null(c.Validate(ValueRef.FromFloat32(0.5f)));
        Assert.Null(c.Validate(ValueRef.FromFloat32(0.0f)));
        Assert.Null(c.Validate(ValueRef.FromFloat32(1.0f)));
    }

    [Fact]
    public void BetweenCheck_BelowOrAbove_ReturnsErrorMessage()
    {
        BetweenCheck c = new(0m, 1m);
        string? below = c.Validate(ValueRef.FromFloat32(-0.1f));
        Assert.NotNull(below);
        Assert.Contains("outside", below);

        string? above = c.Validate(ValueRef.FromFloat32(1.5f));
        Assert.NotNull(above);
        Assert.Contains("outside", above);
    }

    [Fact]
    public void BetweenCheck_NullValue_PassesThrough()
    {
        BetweenCheck c = new(0m, 1m);
        Assert.Null(c.Validate(ValueRef.Null(DataKind.Float32)));
    }

    [Fact]
    public void GreaterThanCheck_StrictAndInclusive_Differ()
    {
        GreaterThanCheck strict = new(0m, Inclusive: false);
        GreaterThanCheck inclusive = new(0m, Inclusive: true);

        Assert.NotNull(strict.Validate(ValueRef.FromInt32(0))); // 0 > 0 fails
        Assert.Null(inclusive.Validate(ValueRef.FromInt32(0)));  // 0 >= 0 passes

        Assert.Null(strict.Validate(ValueRef.FromInt32(1)));
        Assert.Null(inclusive.Validate(ValueRef.FromInt32(1)));
    }

    [Fact]
    public void LessThanCheck_InclusivityHonoured()
    {
        LessThanCheck strict = new(10m, Inclusive: false);
        Assert.NotNull(strict.Validate(ValueRef.FromInt32(10)));
        Assert.Null(strict.Validate(ValueRef.FromInt32(9)));
    }

    [Fact]
    public void RangeCheck_OpenAboveClosedBelow_HandlesCorrectly()
    {
        RangeCheck r = new(Min: 0m, Max: 1m, MinInclusive: true, MaxInclusive: false);
        Assert.Null(r.Validate(ValueRef.FromFloat32(0.0f)));   // closed below
        Assert.Null(r.Validate(ValueRef.FromFloat32(0.9999f)));
        Assert.NotNull(r.Validate(ValueRef.FromFloat32(1.0f))); // open above
    }

    [Fact]
    public void RangeCheck_UnboundedSide_IsAccepted()
    {
        RangeCheck r = new(Min: null, Max: 100m);
        Assert.Null(r.Validate(ValueRef.FromInt32(-1_000_000)));
        Assert.NotNull(r.Validate(ValueRef.FromInt32(101)));
    }

    [Fact]
    public void InCheck_StringValues_MatchesExact()
    {
        InCheck c = new(["small", "medium", "large"]);
        Assert.Null(c.Validate(ValueRef.FromString("small")));
        Assert.NotNull(c.Validate(ValueRef.FromString("xl")));
    }

    [Fact]
    public void InCheck_IntegerValuesStringified_RoundTrip()
    {
        // Mirrors how the SQL walker canonicalises numeric IN-lists: the
        // accepted-values list is strings; numeric inputs are coerced via
        // invariant-culture decimal.
        InCheck c = new(["416", "640"]);
        Assert.Null(c.Validate(ValueRef.FromInt32(416)));
        Assert.NotNull(c.Validate(ValueRef.FromInt32(512)));
    }

    [Fact]
    public void RegexCheck_MatchesAndRejects()
    {
        RegexCheck c = new("^[a-z]+$");
        Assert.Null(c.Validate(ValueRef.FromString("abc")));
        Assert.NotNull(c.Validate(ValueRef.FromString("abc123")));
    }

    [Fact]
    public void RegexCheck_NonStringInput_PassesThrough()
    {
        // Type mismatch is the kind system's job — the check just bails so
        // the type-system error wins.
        RegexCheck c = new("^[a-z]+$");
        Assert.Null(c.Validate(ValueRef.FromInt32(42)));
    }

    [Fact]
    public void CustomCheck_ReturnsNull_DeferredToCallerEvaluator()
    {
        // CustomCheck wraps the raw Expression; runtime validation happens at
        // the dispatch site that has an evaluator. The typed-path Validate is
        // documented as a no-op.
        CustomCheck c = new(new LiteralExpression(true));
        Assert.Null(c.Validate(ValueRef.FromInt32(7)));
    }
}
