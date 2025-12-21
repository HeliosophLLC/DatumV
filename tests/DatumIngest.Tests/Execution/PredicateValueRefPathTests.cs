using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Phase 2 of the evaluator ValueRef arc: predicate-context expressions
/// (WHERE / HAVING / etc.) evaluate end-to-end on <see cref="ValueRef"/>
/// without writing intermediate function results to the arena.
/// </summary>
/// <remarks>
/// <para>
/// These tests use literal-only expressions so they don't depend on the
/// pre-existing <c>Row(string[], DataValue[])</c> throw-stub from the
/// arena migration. The point isn't to prove arena bytes are zero
/// (that needs a counter we don't have yet); it's to prove the new
/// dispatch path produces the same boolean results as the old one
/// across the predicate operators that matter — comparisons, AND/OR/NOT,
/// LIKE/ILIKE/REGEXP, IS NULL, arithmetic-in-predicate.
/// </para>
/// </remarks>
public sealed class PredicateValueRefPathTests
{
    private readonly ExpressionEvaluator _evaluator = new(FunctionRegistry.CreateDefault());
    private static readonly EvaluationFrame Frame = default;

    private static LiteralExpression Lit(object? value) => new(value);
    private static FunctionCallExpression Call(string name, params Expression[] args) => new(name, args);
    private static BinaryExpression Bin(Expression l, BinaryOperator op, Expression r) => new(l, op, r);
    private static UnaryExpression Un(UnaryOperator op, Expression operand) => new(op, operand);

    // ─── Comparison through function chain ─────────────────────────────────

    [Fact]
    public async Task Equal_FunctionCallVsLiteral_ReturnsTrue()
    {
        // upper('alice') = 'ALICE'
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.Equal,
            Lit("ALICE"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Equal_FunctionCallVsLiteral_ReturnsFalse()
    {
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.Equal,
            Lit("BOB"));
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task NotEqual_FunctionCall_ReturnsTrue()
    {
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.NotEqual,
            Lit("alice"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Equal_NestedFunctionChain_ReturnsTrue()
    {
        // upper(lower('Alice')) = 'ALICE'
        Expression predicate = Bin(
            Call("upper", Call("lower", Lit("Alice"))),
            BinaryOperator.Equal,
            Lit("ALICE"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Equal_ConcatVsLiteral_ReturnsTrue()
    {
        // concat('hi ', 'there') = 'hi there'
        Expression predicate = Bin(
            Call("concat", Lit("hi "), Lit("there")),
            BinaryOperator.Equal,
            Lit("hi there"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── LIKE / ILIKE / REGEXP through function chain ──────────────────────

    [Fact]
    public async Task Like_FunctionResult_Matches()
    {
        // upper('alice') LIKE 'AL%'
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.Like,
            Lit("AL%"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Like_DoesNotMatch_ReturnsFalse()
    {
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.Like,
            Lit("BO%"));
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task ILike_CaseInsensitiveMatch_ReturnsTrue()
    {
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.ILike,
            Lit("al%"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Regexp_FunctionResult_Matches()
    {
        Expression predicate = Bin(
            Call("upper", Lit("alice")),
            BinaryOperator.Regexp,
            Lit("^AL"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── Compound predicates (AND / OR / NOT) ──────────────────────────────

    [Fact]
    public async Task And_BothFunctionPredicates_True()
    {
        // upper('alice') = 'ALICE' AND lower('Bob') = 'bob'
        Expression predicate = Bin(
            Bin(Call("upper", Lit("alice")), BinaryOperator.Equal, Lit("ALICE")),
            BinaryOperator.And,
            Bin(Call("lower", Lit("Bob")), BinaryOperator.Equal, Lit("bob")));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task And_OneFalse_ShortCircuitsToFalse()
    {
        Expression predicate = Bin(
            Bin(Call("upper", Lit("alice")), BinaryOperator.Equal, Lit("BOB")),
            BinaryOperator.And,
            Bin(Call("lower", Lit("Bob")), BinaryOperator.Equal, Lit("bob")));
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Or_OneTrue_ReturnsTrue()
    {
        Expression predicate = Bin(
            Bin(Call("upper", Lit("alice")), BinaryOperator.Equal, Lit("BOB")),
            BinaryOperator.Or,
            Bin(Call("lower", Lit("Bob")), BinaryOperator.Equal, Lit("bob")));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Not_FunctionPredicate_Inverts()
    {
        Expression predicate = Un(
            UnaryOperator.Not,
            Bin(Call("upper", Lit("alice")), BinaryOperator.Equal, Lit("ALICE")));
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── IS NULL through function chain ────────────────────────────────────

    [Fact]
    public async Task IsNull_FunctionResult_NotNull_ReturnsFalse()
    {
        Expression predicate = new IsNullExpression(
            Call("upper", Lit("alice")),
            Negated: false);
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task IsNotNull_FunctionResult_NotNull_ReturnsTrue()
    {
        Expression predicate = new IsNullExpression(
            Call("upper", Lit("alice")),
            Negated: true);
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task IsNull_NullLiteral_ReturnsTrue()
    {
        Expression predicate = new IsNullExpression(Lit(null), Negated: false);
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── Arithmetic in predicate ───────────────────────────────────────────

    [Fact]
    public async Task Arithmetic_FunctionInComparison_ReturnsTrue()
    {
        // length-equivalent via cast: cast('42', Int32) > 10
        Expression predicate = Bin(
            Call("cast", Lit("42"), new TypeLiteralExpression("Int32")),
            BinaryOperator.GreaterThan,
            Lit(10));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    [Fact]
    public async Task Arithmetic_AddInComparison_ReturnsTrue()
    {
        // 10 + 5 > 12
        Expression predicate = Bin(
            Bin(Lit(10), BinaryOperator.Add, Lit(5)),
            BinaryOperator.GreaterThan,
            Lit(12));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── Null propagation through binary ───────────────────────────────────

    [Fact]
    public async Task Equal_NullOperand_ReturnsFalse()
    {
        // Comparing anything to null yields null which AsBoolean treats as false.
        Expression predicate = Bin(
            Lit(null),
            BinaryOperator.Equal,
            Call("upper", Lit("alice")));
        Assert.False(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }

    // ─── typeof in predicate ───────────────────────────────────────────────

    [Fact]
    public async Task Typeof_EqualsTypeLiteral_ReturnsTrue()
    {
        // typeof('hi') = String
        Expression predicate = Bin(
            Call("typeof", Lit("hi")),
            BinaryOperator.Equal,
            new TypeLiteralExpression("String"));
        Assert.True(await _evaluator.EvaluateAsBooleanAsync(predicate, Frame));
    }
}
