using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

public class ExpressionEvaluatorTests : ServiceTestBase
{
    //rivate readonly Arena _arena = new();
    private readonly ExpressionEvaluator _evaluator = new(FunctionRegistry.CreateDefault(), store: new Arena());

    // ─────────────── Literals ───────────────

    [Fact]
    public void Literal_Integer()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(42), Row.Empty);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void Literal_Float()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(3.14), Row.Empty);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(3.14, result.AsFloat64(), 0.001);
    }

    [Fact]
    public void Literal_String()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression("hello"), Row.Empty);
        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Literal_Null()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(null), Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Literal_Bool_True()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(true), Row.Empty);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── Column references ───────────────

    [Fact]
    public void ColumnReference_ByName()
    {
        Row row = MakeRow(["age"], DataValue.FromFloat32(25f));
        DataValue result = _evaluator.Evaluate(new ColumnReference("age"), row);
        Assert.Equal(25f, result.AsFloat32());
    }

    [Fact]
    public void ColumnReference_Qualified()
    {
        Row row = MakeRow(["t.age"], DataValue.FromFloat32(30f));
        DataValue result = _evaluator.Evaluate(new ColumnReference("t", "age"), row);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void ColumnReference_NotFound_Throws()
    {
        Row row = MakeRow(["name"], DataValue.FromString("test"));
        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(new ColumnReference("missing"), row));
    }

    // ─────────────── Arithmetic ───────────────

    [Fact]
    public void BinaryAdd()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10),
                BinaryOperator.Add,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.Equal(15, result.AsInt32());
    }

    [Fact]
    public void BinarySubtract()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10),
                BinaryOperator.Subtract,
                new LiteralExpression(3)),
            Row.Empty);
        Assert.Equal(7, result.AsInt32());
    }

    [Fact]
    public void BinaryMultiply()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(4),
                BinaryOperator.Multiply,
                new LiteralExpression(6)),
            Row.Empty);
        Assert.Equal(24, result.AsInt32());
    }

    [Fact]
    public void BinaryDivide()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(20),
                BinaryOperator.Divide,
                new LiteralExpression(4)),
            Row.Empty);
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void BinaryDivideByZero_ReturnsNaN()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(1),
                BinaryOperator.Divide,
                new LiteralExpression(0)),
            Row.Empty);
        Assert.True(float.IsNaN(result.AsFloat32()));
    }

    [Fact]
    public void BinaryModulo()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10),
                BinaryOperator.Modulo,
                new LiteralExpression(3)),
            Row.Empty);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public void BinaryModuloByZero_ReturnsNaN()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10f),
                BinaryOperator.Modulo,
                new LiteralExpression(0f)),
            Row.Empty);
        Assert.True(float.IsNaN(result.AsFloat32()));
    }

    [Fact]
    public void BinaryPower()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(2),
                BinaryOperator.Power,
                new LiteralExpression(10)),
            Row.Empty);
        Assert.Equal(1024f, result.AsFloat32());
    }

    // ─────────────── Comparisons ───────────────

    [Fact]
    public void Equal_True()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.Equal,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Equal_False()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.Equal,
                new LiteralExpression(3)),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void NotEqual_True()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.NotEqual,
                new LiteralExpression(3)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LessThan_True()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(3),
                BinaryOperator.LessThan,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void GreaterThan_True()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.GreaterThan,
                new LiteralExpression(3)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LessThanOrEqual()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.LessThanOrEqual,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void GreaterThanOrEqual()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(5),
                BinaryOperator.GreaterThanOrEqual,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StringComparison_Equal()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("abc"),
                BinaryOperator.Equal,
                new LiteralExpression("abc")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void StringComparison_LessThan()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("abc"),
                BinaryOperator.LessThan,
                new LiteralExpression("def")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── Logical operators ───────────────

    [Fact]
    public void And_BothTrue()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(1),
                BinaryOperator.And,
                new LiteralExpression(1)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void And_LeftFalse_ShortCircuits()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(0),
                BinaryOperator.And,
                new LiteralExpression(1)),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Or_LeftTrue_ShortCircuits()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(1),
                BinaryOperator.Or,
                new LiteralExpression(0)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Or_BothFalse()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(0),
                BinaryOperator.Or,
                new LiteralExpression(0)),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Not_True_Becomes_False()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Not, new LiteralExpression(1)),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Not_False_Becomes_True()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Not, new LiteralExpression(0)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Negate()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Negate, new LiteralExpression(42f)),
            Row.Empty);
        Assert.Equal(-42f, result.AsFloat32());
    }

    // ─────────────── NULL propagation ───────────────

    [Fact]
    public void NullPropagation_BinaryAdd()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(null),
                BinaryOperator.Add,
                new LiteralExpression(5)),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void NullPropagation_Unary()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Negate, new LiteralExpression(null)),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    // ─────────────── IN expression ───────────────

    [Fact]
    public void In_Found()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(3),
                [new LiteralExpression(1), new LiteralExpression(2), new LiteralExpression(3)]),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_NotFound()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(4),
                [new LiteralExpression(1), new LiteralExpression(2), new LiteralExpression(3)]),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void In_Negated()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(4),
                [new LiteralExpression(1), new LiteralExpression(2), new LiteralExpression(3)],
                Negated: true),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_NullTarget()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(null),
                [new LiteralExpression(1)]),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void In_NullCandidate_NoMatch_ReturnsNull()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(99),
                [new LiteralExpression(1), new LiteralExpression(null), new LiteralExpression(3)]),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void In_NullCandidate_WithMatch_ReturnsMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(3),
                [new LiteralExpression(null), new LiteralExpression(3)]),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_LiteralHashSet_CachedAcrossRows()
    {
        InExpression inExpr = new(
            new ColumnReference("value"),
            [new LiteralExpression(10), new LiteralExpression(20), new LiteralExpression(30)]);

        Row row1 = MakeRow(["value"], DataValue.FromFloat32(20));
        Row row2 = MakeRow(["value"], DataValue.FromFloat32(99));
        Row row3 = MakeRow(["value"], DataValue.FromFloat32(10));

        Assert.True(_evaluator.Evaluate(inExpr, row1).AsBoolean());
        Assert.False(_evaluator.Evaluate(inExpr, row2).AsBoolean());
        Assert.True(_evaluator.Evaluate(inExpr, row3).AsBoolean());
    }

    [Fact]
    public void In_LargeValueSet_UsesHashLookup()
    {
        List<Expression> values = new();
        for (int i = 0; i < 1000; i++)
        {
            values.Add(new LiteralExpression(i));
        }

        InExpression inExpr = new(new LiteralExpression(999), values);

        DataValue result = _evaluator.Evaluate(inExpr, Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_Negated_LargeValueSet()
    {
        List<Expression> values = new();
        for (int i = 0; i < 500; i++)
        {
            values.Add(new LiteralExpression(i));
        }

        InExpression inExpr = new(new LiteralExpression(9999), values, Negated: true);

        DataValue result = _evaluator.Evaluate(inExpr, Row.Empty);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── BETWEEN expression ───────────────

    [Fact]
    public void Between_InRange()
    {
        DataValue result = _evaluator.Evaluate(
            new BetweenExpression(
                new LiteralExpression(5),
                new LiteralExpression(1),
                new LiteralExpression(10)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Between_OutOfRange()
    {
        DataValue result = _evaluator.Evaluate(
            new BetweenExpression(
                new LiteralExpression(15),
                new LiteralExpression(1),
                new LiteralExpression(10)),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Between_Inclusive()
    {
        DataValue result = _evaluator.Evaluate(
            new BetweenExpression(
                new LiteralExpression(10),
                new LiteralExpression(1),
                new LiteralExpression(10)),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Between_Negated()
    {
        DataValue result = _evaluator.Evaluate(
            new BetweenExpression(
                new LiteralExpression(5),
                new LiteralExpression(1),
                new LiteralExpression(10),
                Negated: true),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    // ─────────────── IS NULL ───────────────

    [Fact]
    public void IsNull_True()
    {
        Row row = MakeRow(["x"], DataValue.Null(DataKind.Float32));
        DataValue result = _evaluator.Evaluate(
            new IsNullExpression(new ColumnReference("x")),
            row);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void IsNull_False()
    {
        Row row = MakeRow(["x"], DataValue.FromFloat32(42f));
        DataValue result = _evaluator.Evaluate(
            new IsNullExpression(new ColumnReference("x")),
            row);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void IsNotNull_True()
    {
        Row row = MakeRow(["x"], DataValue.FromFloat32(42f));
        DataValue result = _evaluator.Evaluate(
            new IsNullExpression(new ColumnReference("x"), Negated: true),
            row);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── LIKE ───────────────

    [Fact]
    public void Like_Percent_Prefix()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("hello world"),
                BinaryOperator.Like,
                new LiteralExpression("hello%")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Like_Percent_Suffix()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("hello world"),
                BinaryOperator.Like,
                new LiteralExpression("%world")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Like_Underscore()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("cat"),
                BinaryOperator.Like,
                new LiteralExpression("c_t")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Like_NoMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("dog"),
                BinaryOperator.Like,
                new LiteralExpression("c_t")),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Like_CaseSensitive_NoMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("HELLO"),
                BinaryOperator.Like,
                new LiteralExpression("hello")),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Like_CaseSensitive_ExactMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("Hello"),
                BinaryOperator.Like,
                new LiteralExpression("Hello")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── ILIKE ───────────────

    [Fact]
    public void ILike_CaseInsensitive_Match()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("HELLO WORLD"),
                BinaryOperator.ILike,
                new LiteralExpression("hello%")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ILike_Wildcards()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("Cat"),
                BinaryOperator.ILike,
                new LiteralExpression("c_t")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ILike_NoMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("dog"),
                BinaryOperator.ILike,
                new LiteralExpression("c_t")),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    // ─────────────── REGEXP ───────────────

    [Fact]
    public void Regexp_SubstringMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("abc123def"),
                BinaryOperator.Regexp,
                new LiteralExpression("\\d+")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Regexp_Anchored()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("555-1234"),
                BinaryOperator.Regexp,
                new LiteralExpression("^\\d{3}-\\d{4}$")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Regexp_NoMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("hello"),
                BinaryOperator.Regexp,
                new LiteralExpression("^\\d+$")),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Regexp_CaseSensitive()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("HELLO"),
                BinaryOperator.Regexp,
                new LiteralExpression("hello")),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Regexp_InlineIgnoreCase()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression("HELLO"),
                BinaryOperator.Regexp,
                new LiteralExpression("(?i)hello")),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Regexp_InvalidPattern_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            _evaluator.Evaluate(
                new BinaryExpression(
                    new LiteralExpression("test"),
                    BinaryOperator.Regexp,
                    new LiteralExpression("[invalid")),
                Row.Empty));
    }

    // ─────────────── LIKE ESCAPE ───────────────

    [Fact]
    public void LikeEscape_LiteralPercent_Matches()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression("100%"),
                new LiteralExpression("100\\%"),
                new LiteralExpression("\\"),
                CaseInsensitive: false),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LikeEscape_LiteralPercent_NoMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression("10099"),
                new LiteralExpression("100\\%"),
                new LiteralExpression("\\"),
                CaseInsensitive: false),
            Row.Empty);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void LikeEscape_LiteralUnderscore_Matches()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression("_test"),
                new LiteralExpression("!_test"),
                new LiteralExpression("!"),
                CaseInsensitive: false),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LikeEscape_MixedWildcardsAndEscape()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression("50% off sale"),
                new LiteralExpression("%\\%%"),
                new LiteralExpression("\\"),
                CaseInsensitive: false),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ILikeEscape_CaseInsensitive()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression("100%"),
                new LiteralExpression("100\\%"),
                new LiteralExpression("\\"),
                CaseInsensitive: true),
            Row.Empty);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void LikeEscape_NullInput_ReturnsNull()
    {
        DataValue result = _evaluator.Evaluate(
            new LikeExpression(
                new LiteralExpression(null),
                new LiteralExpression("pattern"),
                new LiteralExpression("\\"),
                CaseInsensitive: false),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void LikeEscape_InvalidEscapeChar_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _evaluator.Evaluate(
                new LikeExpression(
                    new LiteralExpression("test"),
                    new LiteralExpression("te\\st"),
                    new LiteralExpression("ab"),
                    CaseInsensitive: false),
                Row.Empty));
    }

    // ─────────────── Function calls ───────────────

    [Fact]
    public void FunctionCall_Len()
    {
        Row row = MakeRow(["name"], DataValue.FromString("hello"));
        DataValue result = _evaluator.Evaluate(
            new FunctionCallExpression("len", [new ColumnReference("name")]),
            row);
        Assert.Equal(5, result.AsInt32());
    }

    [Fact]
    public void FunctionCall_Unknown_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(
                new FunctionCallExpression("nonexistent", [new LiteralExpression(1)]),
                Row.Empty));
    }

    // ─────────────── CAST expression ───────────────

    [Fact]
    public void Cast_UInt8ToScalar()
    {
        Row row = MakeRow(["x"], DataValue.FromUInt8(200));
        DataValue result = _evaluator.Evaluate(
            new CastExpression(new ColumnReference("x"), "Float32"),
            row);
        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(200f, result.AsFloat32());
    }

    // ─────────────── EvaluateAsBoolean ───────────────

    [Fact]
    public void EvaluateAsBoolean_NonZero_True()
    {
        Assert.True(_evaluator.EvaluateAsBoolean(new LiteralExpression(1), Row.Empty));
    }

    [Fact]
    public void EvaluateAsBoolean_Zero_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(0), Row.Empty));
    }

    [Fact]
    public void EvaluateAsBoolean_Null_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(null), Row.Empty));
    }

    [Fact]
    public void EvaluateAsBoolean_NonEmptyString_True()
    {
        Assert.True(_evaluator.EvaluateAsBoolean(new LiteralExpression("x"), Row.Empty));
    }

    [Fact]
    public void EvaluateAsBoolean_EmptyString_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(""), Row.Empty));
    }

    // ─────────────── Column expressions with row data ───────────────

    [Fact]
    public void ArithmeticOnColumns()
    {
        Row row = MakeRow(["price", "quantity"], DataValue.FromFloat32(10f), DataValue.FromFloat32(3f));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("price"),
                BinaryOperator.Multiply,
                new ColumnReference("quantity")),
            row);
        Assert.Equal(30f, result.AsFloat32());
    }

    // EvaluateFunction_ImageFunction_DoesNotDisposeSourceRowHandle removed:
    // it tested the legacy ImageHandle-disposal mechanism in EvaluateFunction
    // (DisposeConsumedImageHandles), which was removed when image functions
    // moved to fused pipelines that emit raw bytes at the boundaries.

    [Fact]
    public void ComparisonOnColumns()
    {
        Row row = MakeRow(["age", "threshold"], DataValue.FromFloat32(25f), DataValue.FromFloat32(18f));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("age"),
                BinaryOperator.GreaterThanOrEqual,
                new ColumnReference("threshold")),
            row);
        Assert.True(result.AsBoolean());
    }

    // ─────────────── Duration arithmetic ───────────────

    [Fact]
    public void DurationAdd_ReturnsDuration()
    {
        Row row = MakeRow(
            ["a", "b"],
            DataValue.FromDuration(TimeSpan.FromHours(1)),
            DataValue.FromDuration(TimeSpan.FromMinutes(30)));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("a"),
                BinaryOperator.Add,
                new ColumnReference("b")),
            row);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), result.AsDuration());
    }

    [Fact]
    public void DurationSubtract_ReturnsDuration()
    {
        Row row = MakeRow(
            ["a", "b"],
            DataValue.FromDuration(TimeSpan.FromHours(2)),
            DataValue.FromDuration(TimeSpan.FromMinutes(30)));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("a"),
                BinaryOperator.Subtract,
                new ColumnReference("b")),
            row);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), result.AsDuration());
    }

    [Fact]
    public void DurationSubtract_NegativeResult()
    {
        Row row = MakeRow(
            ["a", "b"],
            DataValue.FromDuration(TimeSpan.FromMinutes(10)),
            DataValue.FromDuration(TimeSpan.FromHours(1)));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("a"),
                BinaryOperator.Subtract,
                new ColumnReference("b")),
            row);

        Assert.Equal(DataKind.Duration, result.Kind);
        Assert.Equal(TimeSpan.FromMinutes(-50), result.AsDuration());
    }

    [Fact]
    public void DurationMultiply_WidensToScalar()
    {
        // Duration * Scalar is not a Duration operation — widens both to float.
        Row row = MakeRow(
            ["d", "n"],
            DataValue.FromDuration(TimeSpan.FromHours(1)),
            DataValue.FromFloat32(2));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("d"),
                BinaryOperator.Multiply,
                new ColumnReference("n")),
            row);

        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(7200f, result.AsFloat32());
    }

    // ─────────────── CASE expression ───────────────

    [Fact]
    public void Case_Searched_MatchesFirstTrueBranch()
    {
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [
                    new WhenClause(new LiteralExpression(false), new LiteralExpression("no")),
                    new WhenClause(new LiteralExpression(true), new LiteralExpression("yes")),
                ],
                new LiteralExpression("default")),
            Row.Empty);
        Assert.Equal("yes", result.AsString());
    }

    [Fact]
    public void Case_Searched_FallsToElse()
    {
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(false), new LiteralExpression("no"))],
                new LiteralExpression("fallback")),
            Row.Empty);
        Assert.Equal("fallback", result.AsString());
    }

    [Fact]
    public void Case_Searched_NoElse_ReturnsNull()
    {
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(false), new LiteralExpression("no"))],
                null),
            Row.Empty);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Case_Simple_MatchesOperand()
    {
        Row row = MakeRow(["status"], DataValue.FromFloat32(2));
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                new ColumnReference("status"),
                [
                    new WhenClause(new LiteralExpression(1), new LiteralExpression("one")),
                    new WhenClause(new LiteralExpression(2), new LiteralExpression("two")),
                ],
                new LiteralExpression("other")),
            row);
        Assert.Equal("two", result.AsString());
    }

    [Fact]
    public void Case_Simple_NoMatch_FallsToElse()
    {
        Row row = MakeRow(["status"], DataValue.FromFloat32(99));
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                new ColumnReference("status"),
                [new WhenClause(new LiteralExpression(1), new LiteralExpression("one"))],
                new LiteralExpression("unknown")),
            row);
        Assert.Equal("unknown", result.AsString());
    }

    [Fact]
    public void Case_Simple_NullOperand_ReturnsNull()
    {
        Row row = MakeRow(["status"], DataValue.Null(DataKind.Float32));
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                new ColumnReference("status"),
                [new WhenClause(new LiteralExpression(1), new LiteralExpression("one"))],
                null),
            row);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Case_Searched_WithColumnCondition()
    {
        Row row = MakeRow(["x"], DataValue.FromFloat32(5));
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [
                    new WhenClause(
                        new BinaryExpression(
                            new ColumnReference("x"),
                            BinaryOperator.GreaterThan,
                            new LiteralExpression(10)),
                        new LiteralExpression("big")),
                    new WhenClause(
                        new BinaryExpression(
                            new ColumnReference("x"),
                            BinaryOperator.GreaterThan,
                            new LiteralExpression(0)),
                        new LiteralExpression("small")),
                ],
                new LiteralExpression("zero")),
            row);
        Assert.Equal("small", result.AsString());
    }

    [Fact]
    public void Case_ShortCircuits_FirstMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [
                    new WhenClause(new LiteralExpression(true), new LiteralExpression("first")),
                    new WhenClause(new LiteralExpression(true), new LiteralExpression("second")),
                ],
                null),
            Row.Empty);
        Assert.Equal("first", result.AsString());
    }

    // ─────────────── CASE mixed-type coercion ───────────────

    [Fact]
    public void Case_MixedStringAndScalar_CoercesStringToScalar()
    {
        // CASE WHEN true THEN '42' ELSE 1 END → '42' is coerced to Int32(42)
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression("42"))],
                new LiteralExpression(1)),
            Row.Empty);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void Case_MixedStringAndScalar_ElseBranchPreservesScalar()
    {
        // CASE WHEN false THEN '0' ELSE 1 END → 1 stays Int32
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(false), new LiteralExpression("0"))],
                new LiteralExpression(1)),
            Row.Empty);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public void Case_MixedStringAndScalar_UnparseableReturnsNull()
    {
        // CASE WHEN true THEN 'abc' ELSE 1 END → 'abc' can't parse as Scalar → null
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression("abc"))],
                new LiteralExpression(1)),
            Row.Empty);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Case_MixedBooleanAndFloat32_CoercesBooleanToFloat64()
    {
        // CASE WHEN true THEN false ELSE 1 END → common kind (Boolean → Int32) is Int32
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression(false))],
                new LiteralExpression(1)),
            Row.Empty);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void Case_NullResult_AdoptsResolvedKind()
    {
        // CASE WHEN false THEN '0' END → no match, no ELSE → null with Int32 kind
        // (String + Int32 unifies to Int32; string values are parsed at runtime).
        Row row = MakeRow(["x"], DataValue.FromFloat32(5));
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [
                    new WhenClause(new LiteralExpression(false), new LiteralExpression("0")),
                    new WhenClause(new LiteralExpression(false), new LiteralExpression(1)),
                ],
                null),
            row);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void Case_AllSameType_NoCoercionNeeded()
    {
        DataValue result = _evaluator.Evaluate(
            new CaseExpression(
                null,
                [new WhenClause(new LiteralExpression(true), new LiteralExpression("yes"))],
                new LiteralExpression("no")),
            Row.Empty);
        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("yes", result.AsString());
    }

    /// <summary>
    /// Reproduces the bug where SUM(CASE WHEN col='x' THEN 1 ELSE 0 END) returns
    /// NULL because the type resolver declared integer literals as Float32 while
    /// the evaluator produced Int32 values. The coercion from Int32 to Float32
    /// failed (no widening path), producing typed nulls that SUM silently skipped.
    /// </summary>
    [Fact]
    public void Case_IntegerLiteral_PreservesInt32Kind()
    {
        Row row = MakeRow(["eval_set"], DataValue.FromString("train"));
        CaseExpression caseExpression = new(
            null,
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("eval_set"),
                        BinaryOperator.Equal,
                        new LiteralExpression("train")),
                    new LiteralExpression(1)),
            ],
            new LiteralExpression(0));

        DataValue result = _evaluator.Evaluate(caseExpression, row);

        Assert.False(result.IsNull, "CASE with integer literals should not produce NULL.");
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(1, result.AsInt32());
    }

    // ─────────────── Struct literal ───────────────

    [Fact]
    public void StructLiteral_TwoFields_ProducesStructValue()
    {
        StructLiteralExpression literal = new(
        [
            new StructField("x", new LiteralExpression(1)),
            new StructField("y", new LiteralExpression(2)),
        ]);

        DataValue result = _evaluator.Evaluate(literal, Row.Empty);

        Assert.Equal(DataKind.Struct, result.Kind);
        Assert.False(result.IsNull);
        DataValue[] fields = result.AsStruct();
        Assert.Equal(2, fields.Length);
        Assert.Equal(1, fields[0].AsInt32());
        Assert.Equal(2, fields[1].AsInt32());
    }

    [Fact]
    public void StructLiteral_WithColumnReferences_CapturesRowValues()
    {
        Row row = MakeRow(["a", "b"], DataValue.FromFloat32(3.14f), DataValue.FromString("hi"));

        StructLiteralExpression literal = new(
        [
            new StructField("val", new ColumnReference("a")),
            new StructField("tag", new ColumnReference("b")),
        ]);

        DataValue result = _evaluator.Evaluate(literal, row);

        DataValue[] fields = result.AsStruct();
        Assert.Equal(3.14f, fields[0].AsFloat32(), precision: 4);
        Assert.Equal("hi", fields[1].AsString());
    }

    // ─────────────── Index access on struct literal ───────────────

    [Fact]
    public void IndexAccess_StructLiteral_ReturnsFieldByName()
    {
        // {x: 10, y: 20}['y']
        IndexAccessExpression access = new(
            new StructLiteralExpression(
            [
                new StructField("x", new LiteralExpression(10)),
                new StructField("y", new LiteralExpression(20)),
            ]),
            new LiteralExpression("y"));

        DataValue result = _evaluator.Evaluate(access, Row.Empty);

        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(20, result.AsInt32());
    }

    [Fact]
    public void IndexAccess_StructLiteral_UnknownField_ReturnsNull()
    {
        // {x: 1}['z']  — field 'z' does not exist
        IndexAccessExpression access = new(
            new StructLiteralExpression(
            [
                new StructField("x", new LiteralExpression(1)),
            ]),
            new LiteralExpression("z"));

        DataValue result = _evaluator.Evaluate(access, Row.Empty);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void IndexAccess_StructLiteral_FieldNameLookupIsCaseInsensitive()
    {
        // {Foo: 42}['foo']
        IndexAccessExpression access = new(
            new StructLiteralExpression(
            [
                new StructField("Foo", new LiteralExpression(42)),
            ]),
            new LiteralExpression("foo"));

        DataValue result = _evaluator.Evaluate(access, Row.Empty);

        Assert.Equal(42, result.AsInt32());
    }

    // ─────────────── Index access on struct column reference ───────────────

    [Fact]
    public void IndexAccess_StructColumnReference_ResolvesFieldViaSchema()
    {
        // Row has a struct column "info" with fields [name, score].
        // Access info['score'] — evaluator needs schema to know field positions.
        Arena arena = new();
        DataValue structValue = DataValue.FromStruct(
            [DataValue.FromString("alice"), DataValue.FromFloat32(9.5f)],
            arena);

        Row row = MakeRow(["info"], structValue);

        ColumnInfo[] fieldInfos =
        [
            new ColumnInfo("name", DataKind.String, false),
            new ColumnInfo("score", DataKind.Float32, false),
        ];
        ColumnInfo structColumn = new("info", false, fieldInfos);
        Schema schema = new([structColumn]);

        ExpressionEvaluator evaluatorWithSchema = new(FunctionRegistry.CreateDefault(), sourceSchema: schema);

        IndexAccessExpression access = new(
            new ColumnReference("info"),
            new LiteralExpression("score"));

        DataValue result = evaluatorWithSchema.Evaluate(access, row);

        Assert.Equal(DataKind.Float32, result.Kind);
        Assert.Equal(9.5f, result.AsFloat32(), precision: 4);
    }

    // ─────────────── AT TIME ZONE ───────────────

    [Fact]
    public void AtTimeZone_UtcToEasternStandardTime()
    {
        // 2026-01-15 12:00 UTC → 2026-01-15 07:00 EST (-05:00)
        DateTimeOffset utc = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        Row row = MakeRow(["ts"], DataValue.FromDateTime(utc));

        Expression expr = new AtTimeZoneExpression(
            new ColumnReference("ts"),
            new LiteralExpression("America/New_York"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.DateTime, result.Kind);
        DateTimeOffset converted = result.ToDateTimeOffset();
        Assert.Equal(7, converted.Hour);
        Assert.Equal(new TimeSpan(-5, 0, 0), converted.Offset);
    }

    [Fact]
    public void AtTimeZone_UtcToEasternDaylightTime()
    {
        // 2026-07-15 12:00 UTC → 2026-07-15 08:00 EDT (-04:00)
        DateTimeOffset utc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        Row row = MakeRow(["ts"], DataValue.FromDateTime(utc));

        Expression expr = new AtTimeZoneExpression(
            new ColumnReference("ts"),
            new LiteralExpression("America/New_York"));

        DataValue result = _evaluator.Evaluate(expr, row);

        DateTimeOffset converted = result.ToDateTimeOffset();
        Assert.Equal(8, converted.Hour);
        Assert.Equal(new TimeSpan(-4, 0, 0), converted.Offset);
    }

    [Fact]
    public void AtTimeZone_NullInputReturnsNull()
    {
        Row row = MakeRow(["ts"], DataValue.Null(DataKind.DateTime));

        Expression expr = new AtTimeZoneExpression(
            new ColumnReference("ts"),
            new LiteralExpression("America/New_York"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.DateTime, result.Kind);
    }

    [Fact]
    public void AtTimeZone_RoundTripsBackToUtc()
    {
        // Convert to New_York and back to UTC — should get the same instant.
        DateTimeOffset utc = new(2026, 6, 15, 18, 0, 0, TimeSpan.Zero);
        Row row = MakeRow(["ts"], DataValue.FromDateTime(utc));

        Expression toNy = new AtTimeZoneExpression(
            new ColumnReference("ts"),
            new LiteralExpression("America/New_York"));

        Expression backToUtc = new AtTimeZoneExpression(
            toNy,
            new LiteralExpression("UTC"));

        DataValue result = _evaluator.Evaluate(backToUtc, row);

        Assert.Equal(utc.UtcTicks, result.ToDateTimeOffset().UtcTicks);
    }

    [Fact]
    public void AtTimeZone_InvalidTimezone_Throws()
    {
        Row row = MakeRow(["ts"], DataValue.FromDateTime(DateTimeOffset.UtcNow));

        Expression expr = new AtTimeZoneExpression(
            new ColumnReference("ts"),
            new LiteralExpression("Not/AZone"));

        Assert.Throws<TimeZoneNotFoundException>(() => _evaluator.Evaluate(expr, row));
    }

    // ─────────────── typeof() and type literals ───────────────

    [Fact]
    public void Typeof_ReturnsTypeTag()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));
        Expression expr = new FunctionCallExpression("typeof", [new ColumnReference("x")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(DataKind.Int32, result.AsType());
    }

    [Fact]
    public void Typeof_StringColumn_ReturnsStringType()
    {
        Row row = MakeRow(["x"], DataValue.FromString("hello"));
        Expression expr = new FunctionCallExpression("typeof", [new ColumnReference("x")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(DataKind.String, result.AsType());
    }

    [Fact]
    public void TypeLiteral_ProducesTypeValue()
    {
        Row row = Row.Empty;
        Expression expr = new TypeLiteralExpression("Int32");

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Type, result.Kind);
        Assert.Equal(DataKind.Int32, result.AsType());
    }

    [Fact]
    public void Typeof_EqualsTypeLiteral_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("typeof", [new ColumnReference("x")]),
            BinaryOperator.Equal,
            new TypeLiteralExpression("Int32"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Boolean, result.Kind);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Typeof_NotEqualsTypeLiteral_ReturnsFalse()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("typeof", [new ColumnReference("x")]),
            BinaryOperator.Equal,
            new TypeLiteralExpression("Float64"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Boolean, result.Kind);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Typeof_DisplayString_ShowsTypeName()
    {
        DataValue typeValue = DataValue.FromType(DataKind.Float64);

        Assert.Equal("Float64", typeValue.ToDisplayString());
    }

    // ─────────────── IS [NOT] Type (desugared) ───────────────

    [Fact]
    public void IsType_Desugared_MatchingType_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));

        // x IS Int32 desugars to: typeof(x) = Int32
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("typeof", [new ColumnReference("x")]),
            BinaryOperator.Equal,
            new TypeLiteralExpression("Int32"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void IsNotType_Desugared_DifferentType_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromString("hello"));

        // x IS NOT Int32 desugars to: typeof(x) != Int32
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("typeof", [new ColumnReference("x")]),
            BinaryOperator.NotEqual,
            new TypeLiteralExpression("Int32"));

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.AsBoolean());
    }

    // ─────────────── can_cast() ───────────────

    [Fact]
    public void CanCast_SameType_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_IntFitsInUInt8_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(200));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("UInt8")]);

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_IntOverflowsUInt8_ReturnsFalse()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(5000));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("UInt8")]);

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_NegativeToUnsigned_ReturnsFalse()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(-1));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("UInt8")]);

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_FloatToInt_WithFraction_ReturnsTrue()
    {
        // Truncation is allowed — only overflow returns false.
        // can_cast(3.14, Int32) is true because CAST(3.14 AS Int32) succeeds (returns 3).
        Row row = MakeRow(["x"], DataValue.FromFloat64(3.14));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_FloatToInt_WholeNumber_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromFloat64(42.0));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_ValidDateString_ReturnsTrue()
    {
        Row row = MakeRow(["x"], DataValue.FromString("2024-06-15"));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Date")]);

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_InvalidDateString_ReturnsFalse()
    {
        Row row = MakeRow(["x"], DataValue.FromString("not-a-date"));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Date")]);

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void CanCast_UnsupportedPair_ReturnsFalse()
    {
        Row row = MakeRow(["x"], DataValue.FromInlineArray<float>([1f, 2f, 3f], DataKind.Float32));
        Expression expr = new FunctionCallExpression("can_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    // ─────────────── try_cast() ───────────────

    [Fact]
    public void TryCast_ValidConversion_ReturnsValue()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Float64")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(42.0, result.AsFloat64());
    }

    [Fact]
    public void TryCast_InvalidStringToInt_ReturnsNull()
    {
        Row row = MakeRow(["x"], DataValue.FromString("not_a_number"));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public void TryCast_ValidStringToDate_ReturnsDate()
    {
        Row row = MakeRow(["x"], DataValue.FromString("2024-06-15"));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Date")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
        Assert.Equal(new DateOnly(2024, 6, 15), result.AsDate());
    }

    [Fact]
    public void TryCast_InvalidStringToDate_ReturnsNull()
    {
        Row row = MakeRow(["x"], DataValue.FromString("garbage"));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Date")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Date, result.Kind);
    }

    [Fact]
    public void TryCast_NumericTruncation_Succeeds()
    {
        // try_cast follows CAST semantics — truncation is allowed
        Row row = MakeRow(["x"], DataValue.FromFloat64(3.99));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void TryCast_UnsupportedPair_ReturnsNull()
    {
        Row row = MakeRow(["x"], DataValue.FromInlineArray<float>([1f, 2f, 3f], DataKind.Float32));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void TryCast_NullInput_ReturnsTypedNull()
    {
        Row row = MakeRow(["x"], DataValue.Null(DataKind.String));
        Expression expr = new FunctionCallExpression("try_cast",
            [new ColumnReference("x"), new TypeLiteralExpression("Int32")]);

        DataValue result = _evaluator.Evaluate(expr, row);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ─────────────── Type-narrowing bind (desugared with can_cast) ───────────────

    [Fact]
    public void TypeNarrow_MatchingType_GuardPassesAndCastApplies()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(42));

        // x AS Int32 y AND y > 0 desugars to:
        // can_cast(x, Int32) AND CAST(x AS Int32) > 0
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("can_cast",
                [new ColumnReference("x"), new TypeLiteralExpression("Int32")]),
            BinaryOperator.And,
            new BinaryExpression(
                new CastExpression(new ColumnReference("x"), "Int32"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        Assert.True(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void TypeNarrow_WrongType_GuardFailsAndShortCircuits()
    {
        Row row = MakeRow(["x"], DataValue.FromString("hello"));

        // can_cast(x, Int32) AND CAST(x AS Int32) > 0
        // The guard fails ("hello" can't be cast to Int32), AND short-circuits
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("can_cast",
                [new ColumnReference("x"), new TypeLiteralExpression("Int32")]),
            BinaryOperator.And,
            new BinaryExpression(
                new CastExpression(new ColumnReference("x"), "Int32"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    [Fact]
    public void TypeNarrow_ValueOutOfRange_GuardFailsAndShortCircuits()
    {
        Row row = MakeRow(["x"], DataValue.FromInt32(5000));

        // x AS UInt8 y AND y > 0 desugars to:
        // can_cast(x, UInt8) AND CAST(x AS UInt8) > 0
        // 5000 doesn't fit in UInt8, so can_cast returns false
        Expression expr = new BinaryExpression(
            new FunctionCallExpression("can_cast",
                [new ColumnReference("x"), new TypeLiteralExpression("UInt8")]),
            BinaryOperator.And,
            new BinaryExpression(
                new CastExpression(new ColumnReference("x"), "UInt8"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        Assert.False(_evaluator.EvaluateAsBoolean(expr, row));
    }

    // ─────────────── Source span error enrichment ───────────────

    [Fact]
    public void Error_IncludesSourceSpan_WhenExpressionHasSpan()
    {
        // A CAST expression with a known span that will fail at runtime
        // (casting a string that isn't a valid number to Int32).
        var span = new SourceSpan(14, 5, 20);
        var expr = new CastExpression(
            new LiteralExpression("not_a_number"), "Int32", span);

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => _evaluator.Evaluate(expr, Row.Empty));

        Assert.Equal(span, ex.Span);
        Assert.Contains("Line 14", ex.Message);
        Assert.Contains("Col 5", ex.Message);
    }

    [Fact]
    public void Error_IncludesSourceSpan_FromFunctionCall()
    {
        // date_add() with a String amount — not numeric, triggers validation error.
        var span = new SourceSpan(7, 12, 30);
        var expr = new FunctionCallExpression("date_add",
            [
                new LiteralExpression("day"),
                new LiteralExpression("not_a_number"),
                new LiteralExpression(DataValue.FromDate(new DateOnly(2026, 1, 15))),
            ],
            Span: span);

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => _evaluator.Evaluate(expr, Row.Empty));

        Assert.Equal(span, ex.Span);
        Assert.Contains("Line 7", ex.Message);
        Assert.Contains("Col 12", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Error_FallsBackToChildSpan_ForBinaryExpression()
    {
        // BinaryExpression has no span itself — the enrichment should
        // walk to the left child's span.
        var childSpan = new SourceSpan(3, 10, 5);
        var expr = new BinaryExpression(
            new ColumnReference(null, "missing_col", childSpan),
            BinaryOperator.Add,
            new LiteralExpression(1));

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => _evaluator.Evaluate(expr, Row.Empty));

        Assert.Equal(childSpan, ex.Span);
        Assert.Contains("Line 3", ex.Message);
        Assert.Contains("Col 10", ex.Message);
    }

    [Fact]
    public void Error_DoesNotDoubleWrap_OnRecursiveEvaluation()
    {
        // A nested expression where the inner node has a span and the outer
        // node also has a span — should only wrap once (the innermost catch).
        var innerSpan = new SourceSpan(5, 1, 10);
        var expr = new CastExpression(
            new CastExpression(
                new LiteralExpression("not_a_number"), "Int32", innerSpan),
            "Float32",
            new SourceSpan(5, 20, 15));

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => _evaluator.Evaluate(expr, Row.Empty));

        // The innermost span should be the one reported.
        Assert.Equal(innerSpan, ex.Span);
    }

    [Fact]
    public void Error_RethrowsUnchanged_WhenNoSpanAvailable()
    {
        // LiteralExpression has no span, and the value type (a bare object)
        // is unsupported — should throw without wrapping.
        var expr = new LiteralExpression(new object());

        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(expr, Row.Empty));
    }
}
