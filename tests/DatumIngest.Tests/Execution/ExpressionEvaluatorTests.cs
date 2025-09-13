using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Image;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using SkiaSharp;

namespace DatumIngest.Tests.Execution;

public class ExpressionEvaluatorTests
{
    private readonly ExpressionEvaluator _evaluator = new(FunctionRegistry.CreateDefault());

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    // ─────────────── Literals ───────────────

    [Fact]
    public void Literal_Integer()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(42), MakeRow());
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void Literal_Float()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(3.14), MakeRow());
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(3.14, result.AsFloat64(), 0.001);
    }

    [Fact]
    public void Literal_String()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression("hello"), MakeRow());
        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Literal_Null()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(null), MakeRow());
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Literal_Bool_True()
    {
        DataValue result = _evaluator.Evaluate(new LiteralExpression(true), MakeRow());
        Assert.True(result.AsBoolean());
    }

    // ─────────────── Column references ───────────────

    [Fact]
    public void ColumnReference_ByName()
    {
        Row row = MakeRow(("age", DataValue.FromFloat32(25f)));
        DataValue result = _evaluator.Evaluate(new ColumnReference("age"), row);
        Assert.Equal(25f, result.AsFloat32());
    }

    [Fact]
    public void ColumnReference_Qualified()
    {
        Row row = MakeRow(("t.age", DataValue.FromFloat32(30f)));
        DataValue result = _evaluator.Evaluate(new ColumnReference("t", "age"), row);
        Assert.Equal(30f, result.AsFloat32());
    }

    [Fact]
    public void ColumnReference_NotFound_Throws()
    {
        Row row = MakeRow(("name", DataValue.FromString("test")));
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
            MakeRow());
        Assert.Equal(15f, result.AsFloat32());
    }

    [Fact]
    public void BinarySubtract()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10),
                BinaryOperator.Subtract,
                new LiteralExpression(3)),
            MakeRow());
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void BinaryMultiply()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(4),
                BinaryOperator.Multiply,
                new LiteralExpression(6)),
            MakeRow());
        Assert.Equal(24f, result.AsFloat32());
    }

    [Fact]
    public void BinaryDivide()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(20),
                BinaryOperator.Divide,
                new LiteralExpression(4)),
            MakeRow());
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void BinaryDivideByZero_ReturnsNaN()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(1),
                BinaryOperator.Divide,
                new LiteralExpression(0)),
            MakeRow());
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
            MakeRow());
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void BinaryModuloByZero_ReturnsNaN()
    {
        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new LiteralExpression(10),
                BinaryOperator.Modulo,
                new LiteralExpression(0)),
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Not_True_Becomes_False()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Not, new LiteralExpression(1)),
            MakeRow());
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Not_False_Becomes_True()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Not, new LiteralExpression(0)),
            MakeRow());
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Negate()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Negate, new LiteralExpression(42)),
            MakeRow());
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
            MakeRow());
        Assert.True(result.IsNull);
    }

    [Fact]
    public void NullPropagation_Unary()
    {
        DataValue result = _evaluator.Evaluate(
            new UnaryExpression(UnaryOperator.Negate, new LiteralExpression(null)),
            MakeRow());
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
            MakeRow());
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_NotFound()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(4),
                [new LiteralExpression(1), new LiteralExpression(2), new LiteralExpression(3)]),
            MakeRow());
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
            MakeRow());
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_NullTarget()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(null),
                [new LiteralExpression(1)]),
            MakeRow());
        Assert.True(result.IsNull);
    }

    [Fact]
    public void In_NullCandidate_NoMatch_ReturnsNull()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(99),
                [new LiteralExpression(1), new LiteralExpression(null), new LiteralExpression(3)]),
            MakeRow());
        Assert.True(result.IsNull);
    }

    [Fact]
    public void In_NullCandidate_WithMatch_ReturnsMatch()
    {
        DataValue result = _evaluator.Evaluate(
            new InExpression(
                new LiteralExpression(3),
                [new LiteralExpression(null), new LiteralExpression(3)]),
            MakeRow());
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void In_LiteralHashSet_CachedAcrossRows()
    {
        InExpression inExpr = new(
            new ColumnReference("value"),
            [new LiteralExpression(10), new LiteralExpression(20), new LiteralExpression(30)]);

        Row row1 = MakeRow(("value", DataValue.FromFloat32(20)));
        Row row2 = MakeRow(("value", DataValue.FromFloat32(99)));
        Row row3 = MakeRow(("value", DataValue.FromFloat32(10)));

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

        DataValue result = _evaluator.Evaluate(inExpr, MakeRow());
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

        DataValue result = _evaluator.Evaluate(inExpr, MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
        Assert.False(result.AsBoolean());
    }

    // ─────────────── IS NULL ───────────────

    [Fact]
    public void IsNull_True()
    {
        Row row = MakeRow(("x", DataValue.Null(DataKind.Float32)));
        DataValue result = _evaluator.Evaluate(
            new IsNullExpression(new ColumnReference("x")),
            row);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void IsNull_False()
    {
        Row row = MakeRow(("x", DataValue.FromFloat32(42f)));
        DataValue result = _evaluator.Evaluate(
            new IsNullExpression(new ColumnReference("x")),
            row);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void IsNotNull_True()
    {
        Row row = MakeRow(("x", DataValue.FromFloat32(42f)));
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
                MakeRow()));
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
                MakeRow()));
    }

    // ─────────────── Function calls ───────────────

    [Fact]
    public void FunctionCall_Len()
    {
        Row row = MakeRow(("name", DataValue.FromString("hello")));
        DataValue result = _evaluator.Evaluate(
            new FunctionCallExpression("len", [new ColumnReference("name")]),
            row);
        Assert.Equal(5f, result.AsFloat32());
    }

    [Fact]
    public void FunctionCall_Unknown_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(
                new FunctionCallExpression("nonexistent", [new LiteralExpression(1)]),
                MakeRow()));
    }

    // ─────────────── CAST expression ───────────────

    [Fact]
    public void Cast_UInt8ToScalar()
    {
        Row row = MakeRow(("x", DataValue.FromUInt8(200)));
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
        Assert.True(_evaluator.EvaluateAsBoolean(new LiteralExpression(1), MakeRow()));
    }

    [Fact]
    public void EvaluateAsBoolean_Zero_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(0), MakeRow()));
    }

    [Fact]
    public void EvaluateAsBoolean_Null_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(null), MakeRow()));
    }

    [Fact]
    public void EvaluateAsBoolean_NonEmptyString_True()
    {
        Assert.True(_evaluator.EvaluateAsBoolean(new LiteralExpression("x"), MakeRow()));
    }

    [Fact]
    public void EvaluateAsBoolean_EmptyString_False()
    {
        Assert.False(_evaluator.EvaluateAsBoolean(new LiteralExpression(""), MakeRow()));
    }

    // ─────────────── Column expressions with row data ───────────────

    [Fact]
    public void ArithmeticOnColumns()
    {
        Row row = MakeRow(
            ("price", DataValue.FromFloat32(10f)),
            ("quantity", DataValue.FromFloat32(3f)));

        DataValue result = _evaluator.Evaluate(
            new BinaryExpression(
                new ColumnReference("price"),
                BinaryOperator.Multiply,
                new ColumnReference("quantity")),
            row);
        Assert.Equal(30f, result.AsFloat32());
    }

    /// <summary>
    /// Reproduces the crash from SELECT *, image_to_tensor_chw(image): the function
    /// consumed and disposed the image handle, but the ordinal copy of the image
    /// column (from *) still referenced the same handle, causing an
    /// <see cref="ObjectDisposedException"/> during serialization.
    /// </summary>
    [Fact]
    public void EvaluateFunction_ImageFunction_DoesNotDisposeSourceRowHandle()
    {
        SKBitmap bitmap = new(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
        ImageHandle handle = new(bitmap, SKEncodedImageFormat.Png);
        DataValue imageValue = DataValue.FromImageHandle(handle);
        Row row = MakeRow(("image", imageValue));

        FunctionCallExpression call = new(
            "image_to_tensor_chw",
            new List<Expression> { new ColumnReference("image") });

        DataValue result = _evaluator.Evaluate(call, row);

        Assert.Equal(DataKind.Tensor, result.Kind);

        // The source row's handle must remain usable — ordinal copies need it.
        byte[] encoded = handle.GetEncodedBytes();
        Assert.NotNull(encoded);
    }

    [Fact]
    public void ComparisonOnColumns()
    {
        Row row = MakeRow(
            ("age", DataValue.FromFloat32(25f)),
            ("threshold", DataValue.FromFloat32(18f)));

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
            ("a", DataValue.FromDuration(TimeSpan.FromHours(1))),
            ("b", DataValue.FromDuration(TimeSpan.FromMinutes(30))));

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
            ("a", DataValue.FromDuration(TimeSpan.FromHours(2))),
            ("b", DataValue.FromDuration(TimeSpan.FromMinutes(30))));

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
            ("a", DataValue.FromDuration(TimeSpan.FromMinutes(10))),
            ("b", DataValue.FromDuration(TimeSpan.FromHours(1))));

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
            ("d", DataValue.FromDuration(TimeSpan.FromHours(1))),
            ("n", DataValue.FromFloat32(2)));

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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Case_Simple_MatchesOperand()
    {
        Row row = MakeRow(("status", DataValue.FromFloat32(2)));
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
        Row row = MakeRow(("status", DataValue.FromFloat32(99)));
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
        Row row = MakeRow(("status", DataValue.Null(DataKind.Float32)));
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
        Row row = MakeRow(("x", DataValue.FromFloat32(5)));
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
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
            MakeRow());
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void Case_NullResult_AdoptsResolvedKind()
    {
        // CASE WHEN false THEN '0' END → no match, no ELSE → null with Int32 kind
        // (String + Int32 unifies to Int32; string values are parsed at runtime).
        Row row = MakeRow(("x", DataValue.FromFloat32(5)));
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
            MakeRow());
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
        Row row = MakeRow(("eval_set", DataValue.FromString("train")));
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
}
