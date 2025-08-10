using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="WordCountFunction"/> — whitespace-separated word counting.
/// </summary>
public sealed class WordCountFunctionTests
{
    private readonly WordCountFunction _function = new();

    [Fact]
    public void WordCount_SingleWord_Returns1()
    {
        DataValue result = _function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void WordCount_MultipleWords_ReturnsCorrectCount()
    {
        DataValue result = _function.Execute([DataValue.FromString("hello world foo")]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void WordCount_EmptyString_Returns0()
    {
        DataValue result = _function.Execute([DataValue.FromString("")]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void WordCount_WhitespaceOnly_Returns0()
    {
        DataValue result = _function.Execute([DataValue.FromString("   \t\n  ")]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void WordCount_LeadingAndTrailingWhitespace_IgnoresThem()
    {
        DataValue result = _function.Execute([DataValue.FromString("  hello world  ")]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void WordCount_MultipleSpacesBetweenWords_CountsCorrectly()
    {
        DataValue result = _function.Execute([DataValue.FromString("a    b\t\tc")]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void WordCount_MixedWhitespace_CountsCorrectly()
    {
        DataValue result = _function.Execute([DataValue.FromString("one\ttwo\nthree\r\nfour")]);
        Assert.Equal(4f, result.AsScalar());
    }

    [Fact]
    public void WordCount_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void WordCount_ValidateArguments_RejectsWrongType()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void WordCount_ValidateArguments_RejectsTooManyArgs()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void WordCount_ValidateArguments_AcceptsString()
    {
        DataKind result = _function.ValidateArguments([DataKind.String]);
        Assert.Equal(DataKind.Scalar, result);
    }
}
