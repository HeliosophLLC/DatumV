using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Strings;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Strings;

public sealed class RegexpLikeFunctionTests
{
    [Fact]
    public void Metadata_ExposesFields()
    {
        Assert.Equal("regexp_like", RegexpLikeFunction.Name);
        Assert.Equal(FunctionCategory.String, RegexpLikeFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(RegexpLikeFunction.Description));
    }

    [Fact]
    public void Registered()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.IsType<RegexpLikeFunction>(registry.TryGetScalar("regexp_like"));
    }

    [Theory]
    [InlineData(new[] { DataKind.String, DataKind.String })]
    [InlineData(new[] { DataKind.String, DataKind.String, DataKind.String })]
    public void Validate_AcceptsOverloads(DataKind[] kinds)
    {
        Assert.Equal(DataKind.Boolean, new RegexpLikeFunction().ValidateArguments(kinds));
    }

    [Fact]
    public void Validate_RejectsTooFewArgs()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpLikeFunction().ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void Validate_RejectsTooManyArgs()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpLikeFunction().ValidateArguments(
                [DataKind.String, DataKind.String, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Validate_RejectsNonStringSource()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new RegexpLikeFunction().ValidateArguments([DataKind.Int32, DataKind.String]));
    }

    private static async Task<bool?> Exec(params ValueRef[] args)
    {
        ValueRef result = await new RegexpLikeFunction().ExecuteAsync(args, default, default);
        if (result.IsNull) return null;
        return result.AsBoolean();
    }

    [Fact]
    public async Task MatchesAnywhereInString()
    {
        Assert.True(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("World")));
    }

    [Fact]
    public async Task ReturnsFalseWhenNoMatch()
    {
        Assert.False(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("xyz")));
    }

    [Fact]
    public async Task AnchoredPatternMatches()
    {
        Assert.True(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("^Hello")));
        Assert.True(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("World$")));
    }

    [Fact]
    public async Task CaseInsensitiveFlag()
    {
        Assert.True(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("world$"),
            ValueRef.FromString("i")));
        Assert.False(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("world$")));
    }

    [Fact]
    public async Task CaseSensitiveFlagOverridesEarlierI()
    {
        Assert.False(await Exec(
            ValueRef.FromString("Hello World"),
            ValueRef.FromString("world"),
            ValueRef.FromString("ic")));
    }

    [Fact]
    public async Task ExtendedFlagIgnoresWhitespaceInPattern()
    {
        Assert.True(await Exec(
            ValueRef.FromString("abc123"),
            ValueRef.FromString("abc \\d+"),
            ValueRef.FromString("x")));
    }

    [Fact]
    public async Task NewlineSensitiveFlagAnchorsPerLine()
    {
        // With 'n' (multiline), $ anchors at line end, not just string end.
        Assert.True(await Exec(
            ValueRef.FromString("foo\nbar"),
            ValueRef.FromString("foo$"),
            ValueRef.FromString("n")));
        // Without 'n', $ anchors only at the end of the string.
        Assert.False(await Exec(
            ValueRef.FromString("foo\nbar"),
            ValueRef.FromString("foo$")));
    }

    [Fact]
    public async Task NullInputPropagatesNull()
    {
        Assert.Null(await Exec(
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("x")));
        Assert.Null(await Exec(
            ValueRef.FromString("abc"),
            ValueRef.Null(DataKind.String)));
        Assert.Null(await Exec(
            ValueRef.FromString("abc"),
            ValueRef.FromString("a"),
            ValueRef.Null(DataKind.String)));
    }

    [Fact]
    public async Task InvalidFlagThrows()
    {
        await Assert.ThrowsAsync<FunctionArgumentException>(() => Exec(
            ValueRef.FromString("abc"),
            ValueRef.FromString("a"),
            ValueRef.FromString("z")));
    }
}
