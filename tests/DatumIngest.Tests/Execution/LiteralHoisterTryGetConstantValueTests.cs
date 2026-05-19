using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Unit tests for <see cref="LiteralHoister.TryGetConstantValue"/>, the helper
/// the table-valued-function validation hook uses to surface plan-time-known
/// constant arguments to TVFs. Covers the literal recognition rules — what
/// counts as a constant and what doesn't — independently of any catalog
/// plumbing.
/// </summary>
public sealed class LiteralHoisterTryGetConstantValueTests
{
    private readonly ByteArrayValueStore _store = new();

    [Fact]
    public void StringLiteral_ReturnsTrue_WithStringValue()
    {
        // Long enough to force the arena path rather than the inline-bytes
        // path, so the store is exercised.
        const string path = "/data/some/long/path/to/a/file.fits";
        LiteralExpression literal = new(path);

        bool ok = LiteralHoister.TryGetConstantValue(literal, _store, out DataValue value);

        Assert.True(ok);
        Assert.Equal(DataKind.String, value.Kind);
        Assert.Equal(path, value.AsString(_store));
    }

    [Fact]
    public void IntLiteral_ReturnsTrue_WithIntValue()
    {
        LiteralExpression literal = new(42);

        bool ok = LiteralHoister.TryGetConstantValue(literal, _store, out DataValue value);

        Assert.True(ok);
        Assert.Equal(DataKind.Int32, value.Kind);
        Assert.Equal(42, value.AsInt32());
    }

    [Fact]
    public void BoolLiteral_ReturnsTrue_WithBoolValue()
    {
        LiteralExpression literal = new(true);

        bool ok = LiteralHoister.TryGetConstantValue(literal, _store, out DataValue value);

        Assert.True(ok);
        Assert.Equal(DataKind.Boolean, value.Kind);
        Assert.True(value.AsBoolean());
    }

    [Fact]
    public void NullLiteral_ReturnsTrue_WithUnknownNull()
    {
        LiteralExpression literal = new((object?)null);

        bool ok = LiteralHoister.TryGetConstantValue(literal, _store, out DataValue value);

        Assert.True(ok);
        Assert.True(value.IsNull);
    }

    [Fact]
    public void LiteralValueExpression_ReturnsTrue_PassesValueThrough()
    {
        // Already-hoisted literal: the hoister should return the carried
        // DataValue verbatim without re-hoisting against the store.
        DataValue original = DataValue.FromInt64(12345);
        LiteralValueExpression hoisted = new(original);

        bool ok = LiteralHoister.TryGetConstantValue(hoisted, _store, out DataValue value);

        Assert.True(ok);
        Assert.Equal(DataKind.Int64, value.Kind);
        Assert.Equal(12345L, value.AsInt64());
    }

    [Fact]
    public void ColumnReference_ReturnsFalse()
    {
        ColumnReference column = new(TableName: null, ColumnName: "some_column");

        bool ok = LiteralHoister.TryGetConstantValue(column, _store, out _);

        Assert.False(ok);
    }

    [Fact]
    public void BinaryExpressionOverLiterals_ReturnsFalse()
    {
        // We do NOT fold constant binary expressions in v1. 1 + 2 is a
        // BinaryExpression, not a LiteralExpression, even though both sides
        // are literals. Recipe authors who need a folded value should either
        // inline it as one literal or pass a bound parameter.
        BinaryExpression binary = new(
            new LiteralExpression(1),
            BinaryOperator.Add,
            new LiteralExpression(2));

        bool ok = LiteralHoister.TryGetConstantValue(binary, _store, out _);

        Assert.False(ok);
    }

    [Fact]
    public void UnboundParameterExpression_ReturnsFalse()
    {
        // $param before ParameterBinder.Bind runs — the planner / resolver
        // path it'd flow through wouldn't see a literal here. After Bind, the
        // ParameterExpression has been rewritten into a LiteralExpression and
        // would match the literal case above; that flow is exercised by the
        // QuerySchemaResolverConstantArgsTests integration tests.
        ParameterExpression parameter = new("archive");

        bool ok = LiteralHoister.TryGetConstantValue(parameter, _store, out _);

        Assert.False(ok);
    }
}
