using System.Globalization;
using DatumQuery.Functions.Scalar;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions;

public class CastFunctionTests
{
    private readonly CastFunction _function = new();

    [Fact]
    public void Name_IsCast()
    {
        Assert.Equal("cast", _function.Name);
    }

    [Fact]
    public void Cast_UInt8ToScalar()
    {
        DataValue result = _function.Execute([
            DataValue.FromUInt8(42),
            DataValue.FromString("Scalar")
        ]);
        Assert.Equal(42f, result.AsScalar());
    }

    [Fact]
    public void Cast_ScalarToUInt8()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(200),
            DataValue.FromString("UInt8")
        ]);
        Assert.Equal((byte)200, result.AsUInt8());
    }

    [Fact]
    public void Cast_ScalarToUInt8_Clamps()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(300),
            DataValue.FromString("UInt8")
        ]);
        Assert.Equal((byte)255, result.AsUInt8());
    }

    [Fact]
    public void Cast_StringToScalar()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("3.14"),
            DataValue.FromString("Scalar")
        ]);
        Assert.Equal(3.14f, result.AsScalar(), 0.001f);
    }

    [Fact]
    public void Cast_ScalarToString()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(42),
            DataValue.FromString("String")
        ]);
        Assert.Equal("42", result.AsString());
    }

    [Fact]
    public void Cast_DateToDateTime()
    {
        DateOnly date = new(2024, 1, 15);
        DataValue result = _function.Execute([
            DataValue.FromDate(date),
            DataValue.FromString("DateTime")
        ]);
        Assert.Equal(new DateTime(2024, 1, 15), result.AsDateTime());
    }

    [Fact]
    public void Cast_DateTimeToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromDateTime(new DateTime(2024, 6, 15, 10, 30, 0)),
            DataValue.FromString("Date")
        ]);
        Assert.Equal(new DateOnly(2024, 6, 15), result.AsDate());
    }

    [Fact]
    public void Cast_StringToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("2024-01-15"),
            DataValue.FromString("Date")
        ]);
        Assert.Equal(new DateOnly(2024, 1, 15), result.AsDate());
    }

    [Fact]
    public void Cast_StringToJsonValue()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("{\"key\": 1}"),
            DataValue.FromString("JsonValue")
        ]);
        Assert.Equal("{\"key\": 1}", result.AsJsonValue());
    }

    [Fact]
    public void Cast_JsonValueToString()
    {
        DataValue result = _function.Execute([
            DataValue.FromJsonValue("{\"key\": 1}"),
            DataValue.FromString("String")
        ]);
        Assert.Equal("{\"key\": 1}", result.AsString());
    }

    [Fact]
    public void Cast_UInt8ArrayToImage()
    {
        byte[] bytes = [1, 2, 3];
        DataValue result = _function.Execute([
            DataValue.FromUInt8Array(bytes),
            DataValue.FromString("Image")
        ]);
        Assert.Equal(DataKind.Image, result.Kind);
        Assert.Equal(bytes, result.AsImage());
    }

    [Fact]
    public void Cast_SameKind_ReturnsSameValue()
    {
        DataValue original = DataValue.FromScalar(42);
        DataValue result = _function.Execute([
            original,
            DataValue.FromString("Scalar")
        ]);
        Assert.Equal(42f, result.AsScalar());
    }

    [Fact]
    public void Cast_NullInput_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("Scalar")
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void Cast_UnknownKindName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromScalar(42),
            DataValue.FromString("Unknown")
        ]));
    }

    [Fact]
    public void Cast_UnsupportedConversion_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _function.Execute([
            DataValue.FromVector([1, 2, 3]),
            DataValue.FromString("String")
        ]));
    }

    [Fact]
    public void Cast_CaseInsensitiveKindName()
    {
        DataValue result = _function.Execute([
            DataValue.FromUInt8(10),
            DataValue.FromString("scalar")
        ]);
        Assert.Equal(10f, result.AsScalar());
    }

    [Fact]
    public void Cast_DateToScalar_EpochDays()
    {
        // 2000-01-01 is 10957 days after 1970-01-01.
        DataValue result = _function.Execute([
            DataValue.FromDate(new DateOnly(2000, 1, 1)),
            DataValue.FromString("Scalar")
        ]);
        Assert.Equal(10957f, result.AsScalar());
    }

    [Fact]
    public void Cast_DateTimeToScalar_EpochSeconds()
    {
        // 2000-01-01T00:00:00Z is 946684800 seconds after Unix epoch.
        DataValue result = _function.Execute([
            DataValue.FromDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            DataValue.FromString("Scalar")
        ]);
        Assert.Equal(946684800f, result.AsScalar());
    }
}
