using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

/// <summary>
/// Tests for the SQL-quoting family: <see cref="QuoteIdentFunction"/>,
/// <see cref="QuoteLiteralFunction"/>, <see cref="QuoteNullableFunction"/>,
/// and <see cref="ParseIdentFunction"/>.
/// </summary>
public sealed class QuotingFunctionsTests
{
    private static List<string?> ArrayToStringList(ValueRef arrayRef)
    {
        Assert.True(arrayRef.IsArray);
        ReadOnlySpan<ValueRef> elements = arrayRef.GetArrayElements();
        List<string?> result = new(elements.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            result.Add(elements[i].IsNull ? null : elements[i].AsString());
        }
        return result;
    }

    // ─── quote_ident ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("my table", "\"my table\"")]
    [InlineData("Foo", "\"Foo\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("", "\"\"")]
    public async Task QuoteIdent_Cases(string input, string expected)
    {
        QuoteIdentFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void QuoteIdent_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<QuoteIdentFunction>(registry.TryGetScalar("quote_ident"));
    }

    // ─── quote_literal ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo", "'foo'")]
    [InlineData("O'Reilly", "'O''Reilly'")]
    [InlineData("", "''")]
    [InlineData("a\\b", "E'a\\\\b'")]
    public async Task QuoteLiteral_Cases(string input, string expected)
    {
        QuoteLiteralFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task QuoteLiteral_NullInput_ReturnsNull()
    {
        QuoteLiteralFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void QuoteLiteral_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<QuoteLiteralFunction>(registry.TryGetScalar("quote_literal"));
    }

    // ─── quote_nullable ────────────────────────────────────────────────────

    [Fact]
    public async Task QuoteNullable_NullInput_ReturnsLiteralNULL()
    {
        QuoteNullableFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) }, default, default);
        Assert.Equal("NULL", result.AsString());
    }

    [Fact]
    public async Task QuoteNullable_NonNull_DelegatesToQuoteLiteral()
    {
        QuoteNullableFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("hi") }, default, default);
        Assert.Equal("'hi'", result.AsString());
    }

    [Fact]
    public void QuoteNullable_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<QuoteNullableFunction>(registry.TryGetScalar("quote_nullable"));
    }

    // ─── parse_ident ───────────────────────────────────────────────────────

    [Fact]
    public async Task ParseIdent_SimpleQualifiedName()
    {
        ParseIdentFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("schema.table") }, default, default);
        Assert.Equal(new[] { "schema", "table" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task ParseIdent_QuotedPreservesCase()
    {
        ParseIdentFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("\"Schema\".\"Table\"") }, default, default);
        Assert.Equal(new[] { "Schema", "Table" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task ParseIdent_UnquotedFoldsToLowercase()
    {
        ParseIdentFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("Foo.BAR") }, default, default);
        Assert.Equal(new[] { "foo", "bar" }, ArrayToStringList(result));
    }

    [Fact]
    public async Task ParseIdent_StrictRejectsTrailingJunk()
    {
        ParseIdentFunction function = new();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            await function.ExecuteAsync(
                new[] { ValueRef.FromString("a.b junk") }, default, default));
    }

    [Fact]
    public async Task ParseIdent_NonStrictTruncatesAtJunk()
    {
        ParseIdentFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("a.b junk"), ValueRef.FromBoolean(false) }, default, default);
        Assert.Equal(new[] { "a", "b" }, ArrayToStringList(result));
    }

    [Fact]
    public void ParseIdent_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<ParseIdentFunction>(registry.TryGetScalar("parse_ident"));
    }
}
