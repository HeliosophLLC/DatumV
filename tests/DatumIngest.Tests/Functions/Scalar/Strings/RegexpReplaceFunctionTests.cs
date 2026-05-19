using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

public sealed class RegexpReplaceFunctionTests
{
    [Fact]
    public void Metadata_ExposesFields()
    {
        Assert.Equal("regexp_replace", RegexpReplaceFunction.Name);
        Assert.Equal(FunctionCategory.String, RegexpReplaceFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(RegexpReplaceFunction.Description));
    }

    [Fact]
    public void Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpReplaceFunction>(registry.TryGetScalar("regexp_replace"));
    }

    [Theory]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String, DataKind.String })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String, DataKind.Int32 })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String, DataKind.Int64, DataKind.Int32 })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String, DataKind.Int32, DataKind.String })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String, DataKind.Int32, DataKind.Int32, DataKind.String })]
    public void Validate_AcceptsAllOverloads(DataKind[] kinds)
    {
        Assert.Equal(DataKind.String, new RegexpReplaceFunction().ValidateArguments(kinds));
    }

    [Fact]
    public void Validate_RejectsTooFewArgs()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpReplaceFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Validate_RejectsTooManyArgs()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpReplaceFunction().ValidateArguments(
                [DataKind.String, DataKind.String, DataKind.String, DataKind.Int32, DataKind.Int32, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Validate_RejectsNonStringSource()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpReplaceFunction().ValidateArguments([DataKind.Int32, DataKind.String, DataKind.String]));
    }

    private static async Task<string?> Exec(params ValueRef[] args)
    {
        ValueRef result = await new RegexpReplaceFunction().ExecuteAsync(args, default, default);
        if (result.IsNull) return null;
        return result.AsString();
    }

    [Fact]
    public async Task ReplacesFirstMatch_ByDefault()
    {
        string? result = await Exec(
            ValueRef.FromString("Mr. Smith and Mr. Jones"),
            ValueRef.FromString("Mr\\."),
            ValueRef.FromString("Dr."));
        Assert.Equal("Dr. Smith and Mr. Jones", result);
    }

    [Fact]
    public async Task GlobalFlag_ReplacesAllMatches()
    {
        string? result = await Exec(
            ValueRef.FromString("Mr. Smith and Mr. Jones"),
            ValueRef.FromString("Mr\\."),
            ValueRef.FromString("Dr."),
            ValueRef.FromString("g"));
        Assert.Equal("Dr. Smith and Dr. Jones", result);
    }

    [Fact]
    public async Task CaseInsensitiveFlag_Matches()
    {
        string? result = await Exec(
            ValueRef.FromString("Hello HELLO hello"),
            ValueRef.FromString("hello"),
            ValueRef.FromString("hi"),
            ValueRef.FromString("ig"));
        Assert.Equal("hi hi hi", result);
    }

    [Fact]
    public async Task BackrefsInReplacement_Pg_Backslash_N()
    {
        string? result = await Exec(
            ValueRef.FromString("John Smith"),
            ValueRef.FromString("(\\w+) (\\w+)"),
            ValueRef.FromString("\\2, \\1"));
        Assert.Equal("Smith, John", result);
    }

    [Fact]
    public async Task BackrefWholeMatch_Backslash_Amp()
    {
        string? result = await Exec(
            ValueRef.FromString("abc 123 xyz"),
            ValueRef.FromString("\\d+"),
            ValueRef.FromString("[\\&]"));
        Assert.Equal("abc [123] xyz", result);
    }

    [Fact]
    public async Task LiteralDollarInReplacement_Escaped()
    {
        string? result = await Exec(
            ValueRef.FromString("price 42"),
            ValueRef.FromString("(\\d+)"),
            ValueRef.FromString("$\\1.00"));
        Assert.Equal("price $42.00", result);
    }

    [Fact]
    public async Task NoMatch_ReturnsInputUnchanged()
    {
        string? result = await Exec(
            ValueRef.FromString("hello world"),
            ValueRef.FromString("xyz"),
            ValueRef.FromString("ZZZ"));
        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task EmptySource_ReturnsEmpty()
    {
        string? result = await Exec(
            ValueRef.FromString(""),
            ValueRef.FromString("foo"),
            ValueRef.FromString("bar"));
        Assert.Equal("", result);
    }

    [Fact]
    public async Task StartArgument_PreservesPrefix_ReplacesFirstAfter()
    {
        // start=5 (1-based) → search begins at the second "foo"; that one is replaced.
        string? result = await Exec(
            ValueRef.FromString("foo foo foo"),
            ValueRef.FromString("foo"),
            ValueRef.FromString("BAR"),
            ValueRef.FromInt32(5));
        Assert.Equal("foo BAR foo", result);
    }

    [Fact]
    public async Task StartArgument_WithGlobalFlag_ReplacesAllAfter()
    {
        string? result = await Exec(
            ValueRef.FromString("foo foo foo"),
            ValueRef.FromString("foo"),
            ValueRef.FromString("BAR"),
            ValueRef.FromInt32(5),
            ValueRef.FromString("g"));
        Assert.Equal("foo BAR BAR", result);
    }

    [Fact]
    public async Task NArgument_ReplacesNthMatchAfterStart()
    {
        // Source: 1 2 3 4 5; start at 1 so all five are in scope; N=3.
        string? result = await Exec(
            ValueRef.FromString("1 2 3 4 5"),
            ValueRef.FromString("\\d"),
            ValueRef.FromString("X"),
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(3));
        Assert.Equal("1 2 X 4 5", result);
    }

    [Fact]
    public async Task NArgument_BeyondMatchCount_LeavesUnchanged()
    {
        string? result = await Exec(
            ValueRef.FromString("a b c"),
            ValueRef.FromString("\\w"),
            ValueRef.FromString("X"),
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(99));
        Assert.Equal("a b c", result);
    }

    [Fact]
    public async Task NArgument_IgnoresGlobalFlag()
    {
        string? result = await Exec(
            ValueRef.FromString("a a a a"),
            ValueRef.FromString("a"),
            ValueRef.FromString("X"),
            ValueRef.FromInt32(1),
            ValueRef.FromInt32(2),
            ValueRef.FromString("g"));
        Assert.Equal("a X a a", result);
    }

    [Fact]
    public async Task StartBeyondLength_ReturnsInputUnchanged()
    {
        string? result = await Exec(
            ValueRef.FromString("hi"),
            ValueRef.FromString("hi"),
            ValueRef.FromString("XX"),
            ValueRef.FromInt32(100));
        Assert.Equal("hi", result);
    }

    [Fact]
    public async Task NewlineSensitiveFlag_DotDoesNotCrossNewline()
    {
        // PG default: '.' matches \n. With 'n', it should not.
        string? defaultBehavior = await Exec(
            ValueRef.FromString("ab\ncd"),
            ValueRef.FromString("a.c"),
            ValueRef.FromString("X"));
        Assert.Equal("ab\ncd", defaultBehavior); // single \n char between b and c, but a.c needs 3 chars

        // More direct: pattern ".+" — default consumes newline; 'n' stops at it.
        string? defaultGreedy = await Exec(
            ValueRef.FromString("ab\ncd"),
            ValueRef.FromString(".+"),
            ValueRef.FromString("X"));
        Assert.Equal("X", defaultGreedy);

        string? newlineSensitive = await Exec(
            ValueRef.FromString("ab\ncd"),
            ValueRef.FromString(".+"),
            ValueRef.FromString("X"),
            ValueRef.FromString("ng"));
        Assert.Equal("X\nX", newlineSensitive);
    }

    [Fact]
    public async Task NullInput_ReturnsNull()
    {
        string? result = await Exec(
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("foo"),
            ValueRef.FromString("bar"));
        Assert.Null(result);
    }

    [Fact]
    public async Task NullPattern_ReturnsNull()
    {
        string? result = await Exec(
            ValueRef.FromString("hello"),
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("bar"));
        Assert.Null(result);
    }

    [Fact]
    public async Task NullReplacement_ReturnsNull()
    {
        string? result = await Exec(
            ValueRef.FromString("hello"),
            ValueRef.FromString("foo"),
            ValueRef.Null(DataKind.String));
        Assert.Null(result);
    }

    [Fact]
    public async Task UnknownFlag_Throws()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(
            () => Exec(
                ValueRef.FromString("a"),
                ValueRef.FromString("a"),
                ValueRef.FromString("b"),
                ValueRef.FromString("z")));
    }
}
