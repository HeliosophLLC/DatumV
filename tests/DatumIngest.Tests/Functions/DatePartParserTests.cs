using DatumIngest.Functions.Scalar;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="DatePartParser"/>.
/// </summary>
public class DatePartParserTests
{
    [Theory]
    [InlineData("year", "year")]
    [InlineData("years", "year")]
    [InlineData("y", "year")]
    [InlineData("quarter", "quarter")]
    [InlineData("quarters", "quarter")]
    [InlineData("q", "quarter")]
    [InlineData("month", "month")]
    [InlineData("months", "month")]
    [InlineData("m", "month")]
    [InlineData("week", "week")]
    [InlineData("weeks", "week")]
    [InlineData("w", "week")]
    [InlineData("day", "day")]
    [InlineData("days", "day")]
    [InlineData("d", "day")]
    [InlineData("hour", "hour")]
    [InlineData("hours", "hour")]
    [InlineData("h", "hour")]
    [InlineData("minute", "minute")]
    [InlineData("minutes", "minute")]
    [InlineData("min", "minute")]
    [InlineData("second", "second")]
    [InlineData("seconds", "second")]
    [InlineData("s", "second")]
    [InlineData("millisecond", "millisecond")]
    [InlineData("milliseconds", "millisecond")]
    [InlineData("ms", "millisecond")]
    public void Parse_RecognizesAllAliases(string input, string expectedCanonical)
    {
        // Verify the alias resolves to the same part as the canonical name.
        // We can't reference the internal enum directly, so we verify via round-trip:
        // parsing the alias and canonical must produce the same result,
        // and that result must not throw (proving it's valid).
        DatePartParser.Parse(input);
        DatePartParser.Parse(expectedCanonical);

        // Both should resolve to the same value — exercise via DateAddFunction as a proxy.
        DateAddFunction function = new();
        DatumIngest.Model.DataValue date = DatumIngest.Model.DataValue.FromDate(new DateOnly(2024, 1, 15));
        DatumIngest.Model.DataValue aliasResult = function.Execute([
            DatumIngest.Model.DataValue.FromString(input),
            DatumIngest.Model.DataValue.FromFloat32(1),
            date
        ]);
        DatumIngest.Model.DataValue canonicalResult = function.Execute([
            DatumIngest.Model.DataValue.FromString(expectedCanonical),
            DatumIngest.Model.DataValue.FromFloat32(1),
            date
        ]);
        Assert.Equal(canonicalResult.AsDate(), aliasResult.AsDate());
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        Assert.Equal(DatePartName.Year, DatePartParser.Parse("YEAR"));
        Assert.Equal(DatePartName.Month, DatePartParser.Parse("Month"));
        Assert.Equal(DatePartName.Day, DatePartParser.Parse("DAY"));
    }

    [Fact]
    public void Parse_UnknownPart_Throws()
    {
        Assert.Throws<ArgumentException>(() => DatePartParser.Parse("unknown"));
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => DatePartParser.Parse(""));
    }
}
