using System.Globalization;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Tests for <see cref="StrftimeFunction"/> and <see cref="IsDateFunction"/>.
/// </summary>
public class DateFormattingFunctionTests
{
    // ───────────────────── strftime() ─────────────────────

    [Fact]
    public void Strftime_Name()
    {
        StrftimeFunction function = new();
        Assert.Equal("strftime", function.Name);
    }

    [Fact]
    public void Strftime_FormatsDate()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromString("yyyy-MM-dd")
        ]);
        Assert.Equal("2024-06-15", result.AsString());
    }

    [Fact]
    public void Strftime_FormatsDateTime()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 14, 30, 45, TimeSpan.Zero)),
            DataValue.FromString("yyyy-MM-dd HH:mm:ss")
        ]);
        Assert.Equal("2024-06-15 14:30:45", result.AsString());
    }

    [Fact]
    public void Strftime_YearOnlyFormat()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromString("yyyy")
        ]);
        Assert.Equal("2024", result.AsString());
    }

    [Fact]
    public void Strftime_MonthDayFormat()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDate(new DateOnly(2024, 6, 15)),
            DataValue.FromString("MM/dd")
        ]);
        Assert.Equal("06/15", result.AsString());
    }

    [Fact]
    public void Strftime_NullDate_ReturnsNull()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Date),
            DataValue.FromString("yyyy-MM-dd")
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Strftime_ReturnsStringKind()
    {
        StrftimeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDate(new DateOnly(2024, 1, 1)),
            DataValue.FromString("yyyy")
        ]);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void Strftime_ValidateArguments_RejectsWrongCount()
    {
        StrftimeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Date]));
    }

    [Fact]
    public void Strftime_ValidateArguments_RejectsNonTemporalFirst()
    {
        StrftimeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Float32, DataKind.String]));
    }

    [Fact]
    public void Strftime_ValidateArguments_RejectsNonStringFormat()
    {
        StrftimeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Date, DataKind.Float32]));
    }

    // ───────────────────── is_date() ─────────────────────

    [Fact]
    public void IsDate_Name()
    {
        IsDateFunction function = new();
        Assert.Equal("is_date", function.Name);
    }

    [Fact]
    public void IsDate_ValidDateString_Returns1()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("2024-01-15")]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_ValidDateTimeString_Returns1()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("2024-01-15T10:30:00Z")]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_InvalidString_Returns0()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("not a date")]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_EmptyString_Returns0()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("")]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_DateInput_Returns1()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromDate(new DateOnly(2024, 1, 15))]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_DateTimeInput_Returns1()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromDateTime(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero))
        ]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_NullInput_ReturnsNull()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void IsDate_Iso8601WithOffset_Returns1()
    {
        IsDateFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("2024-06-15T10:30:00+05:30")]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void IsDate_ValidateArguments_RejectsWrongCount()
    {
        IsDateFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void IsDate_ValidateArguments_RejectsScalar()
    {
        IsDateFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void IsDate_ValidateArguments_AcceptsAllValidKinds()
    {
        IsDateFunction function = new();
        Assert.Equal(DataKind.Float32, function.ValidateArguments([DataKind.String]));
        Assert.Equal(DataKind.Float32, function.ValidateArguments([DataKind.Date]));
        Assert.Equal(DataKind.Float32, function.ValidateArguments([DataKind.DateTime]));
    }
}
