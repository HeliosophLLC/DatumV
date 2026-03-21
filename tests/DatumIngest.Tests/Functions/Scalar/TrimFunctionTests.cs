using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Strings;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="TrimFunction"/>, <see cref="LtrimFunction"/>, and
/// <see cref="RtrimFunction"/> — PG-compatible whitespace/character stripping.
/// </summary>
public sealed class TrimFunctionTests
{
    // ─── trim ──────────────────────────────────────────────────────────────

    [Fact]
    public void Trim_Metadata_ExposesFields()
    {
        Assert.Equal("trim", TrimFunction.Name);
        Assert.Equal(FunctionCategory.String, TrimFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(TrimFunction.Description));
    }

    [Theory]
    [InlineData("  hello  ", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData(" a b ", "a b")]
    public async Task Trim_Execute_DefaultStripsSpaces(string input, string expected)
    {
        TrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, default, default);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("xxhelloxx", "x", "hello")]
    [InlineData("yxxhelloxxy", "xy", "hello")]
    [InlineData("hello", "x", "hello")]
    public async Task Trim_Execute_WithCharSet(string input, string chars, string expected)
    {
        TrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input), ValueRef.FromString(chars) }, default, default);
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Trim_Execute_DoesNotStripTabsOrNewlinesByDefault()
    {
        TrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("\thello\n") }, default, default);
        Assert.Equal("\thello\n", result.AsString());
    }

    [Fact]
    public async Task Trim_Execute_NullInput_ReturnsNull()
    {
        TrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) }, default, default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public async Task Trim_Execute_NullCharSet_ReturnsNull()
    {
        TrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString("xxhelloxx"), ValueRef.Null(DataKind.String) }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Trim_Validate_AcceptsOneOrTwoStrings()
    {
        Assert.Equal(DataKind.String, new TrimFunction().ValidateArguments([DataKind.String]));
        Assert.Equal(DataKind.String, new TrimFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Trim_Validate_RejectsWrongArity()
    {
        Assert.Throws<FunctionArgumentException>(() => new TrimFunction().ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => new TrimFunction().ValidateArguments([DataKind.String, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Trim_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<TrimFunction>(registry.TryGetScalar("trim"));
    }

    [Fact]
    public void Btrim_RegisteredAsAlias()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<TrimFunction>(registry.TryGetScalar("btrim"));
    }

    // ─── ltrim ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("  hello  ", "hello  ")]
    [InlineData("hello", "hello")]
    [InlineData("   ", "")]
    public async Task Ltrim_Execute_DefaultStripsLeadingSpaces(string input, string expected)
    {
        LtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("xxhelloxx", "x", "helloxx")]
    [InlineData("xyxhelloxy", "xy", "helloxy")]
    public async Task Ltrim_Execute_WithCharSet(string input, string chars, string expected)
    {
        LtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input), ValueRef.FromString(chars) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Ltrim_Execute_NullInput_ReturnsNull()
    {
        LtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Ltrim_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<LtrimFunction>(registry.TryGetScalar("ltrim"));
    }

    // ─── rtrim ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("  hello  ", "  hello")]
    [InlineData("hello", "hello")]
    [InlineData("   ", "")]
    public async Task Rtrim_Execute_DefaultStripsTrailingSpaces(string input, string expected)
    {
        RtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(new[] { ValueRef.FromString(input) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Theory]
    [InlineData("xxhelloxx", "x", "xxhello")]
    [InlineData("xyhelloxy", "xy", "xyhello")]
    public async Task Rtrim_Execute_WithCharSet(string input, string chars, string expected)
    {
        RtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.FromString(input), ValueRef.FromString(chars) }, default, default);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public async Task Rtrim_Execute_NullInput_ReturnsNull()
    {
        RtrimFunction function = new();
        ValueRef result = await function.ExecuteAsync(
            new[] { ValueRef.Null(DataKind.String) }, default, default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Rtrim_Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RtrimFunction>(registry.TryGetScalar("rtrim"));
    }
}
