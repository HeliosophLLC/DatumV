using System.Globalization;
using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class CastFunctionTests : ServiceTestBase
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
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(42f, result.AsFloat32());
    }

    [Fact]
    public void Cast_ScalarToUInt8()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(200),
            DataValue.FromString("UInt8")
        ]);
        Assert.Equal((byte)200, result.AsUInt8());
    }

    [Fact]
    public void Cast_ScalarToUInt8_Clamps()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(300),
            DataValue.FromString("UInt8")
        ]);
        Assert.Equal((byte)255, result.AsUInt8());
    }

    [Fact]
    public void Cast_StringToScalar()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("3.14"),
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(3.14f, result.AsFloat32(), 0.001f);
    }

    [Fact]
    public void Cast_ScalarToString()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(42),
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
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero), result.AsDateTime());
    }

    [Fact]
    public void Cast_DateTimeToDate()
    {
        DataValue result = _function.Execute([
            DataValue.FromDateTime(new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero)),
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
        DataValue original = DataValue.FromFloat32(42);
        DataValue result = _function.Execute([
            original,
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(42f, result.AsFloat32());
    }

    [Fact]
    public void Cast_NullInput_ReturnsTypedNull()
    {
        DataValue result = _function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("Float32")
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public void Cast_UnknownKindName_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromFloat32(42),
            DataValue.FromString("NotAType")
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
            DataValue.FromString("float32")
        ]);
        Assert.Equal(10f, result.AsFloat32());
    }

    [Fact]
    public void Cast_DateToScalar_EpochDays()
    {
        // 2000-01-01 is 10957 days after 1970-01-01.
        DataValue result = _function.Execute([
            DataValue.FromDate(new DateOnly(2000, 1, 1)),
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(10957f, result.AsFloat32());
    }

    [Fact]
    public void Cast_DateTimeToScalar_EpochSeconds()
    {
        // 2000-01-01T00:00:00Z is 946684800 seconds after Unix epoch.
        DataValue result = _function.Execute([
            DataValue.FromDateTime(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(946684800f, result.AsFloat32());
    }

    // ── Extended numeric type conversions ────────────────────────────────────

    [Fact]
    public void Cast_BooleanToInt32_True()
    {
        DataValue result = _function.Execute([
            DataValue.FromBoolean(true),
            DataValue.FromString("Int32")
        ]);
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public void Cast_BooleanToInt32_False()
    {
        DataValue result = _function.Execute([
            DataValue.FromBoolean(false),
            DataValue.FromString("Int32")
        ]);
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void Cast_BooleanToInt64_True()
    {
        DataValue result = _function.Execute([
            DataValue.FromBoolean(true),
            DataValue.FromString("Int64")
        ]);
        Assert.Equal(1L, result.AsInt64());
    }

    [Fact]
    public void Cast_BooleanToFloat64()
    {
        DataValue result = _function.Execute([
            DataValue.FromBoolean(true),
            DataValue.FromString("Float64")
        ]);
        Assert.Equal(1.0, result.AsFloat64());
    }

    [Fact]
    public void Cast_Int32ToBoolean_Nonzero_IsTrue()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(42),
            DataValue.FromString("Boolean")
        ]);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Cast_Int32ToBoolean_Zero_IsFalse()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(0),
            DataValue.FromString("Boolean")
        ]);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void Cast_StringToInt32()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("42"),
            DataValue.FromString("Int32")
        ]);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public void Cast_Int32ToString()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(42),
            DataValue.FromString("String")
        ]);
        Assert.Equal("42", result.AsString());
    }

    [Fact]
    public void Cast_Int32ToFloat64()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(42),
            DataValue.FromString("Float64")
        ]);
        Assert.Equal(42.0, result.AsFloat64());
    }

    [Fact]
    public void Cast_Float64ToInt32_Truncates()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat64(3.7),
            DataValue.FromString("Int32")
        ]);
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void Cast_Int32ToInt64()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(int.MaxValue),
            DataValue.FromString("Int64")
        ]);
        Assert.Equal((long)int.MaxValue, result.AsInt64());
    }

    [Fact]
    public void Cast_Int64ToFloat32()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt64(12345L),
            DataValue.FromString("Float32")
        ]);
        Assert.Equal(12345f, result.AsFloat32());
    }

    [Fact]
    public void Cast_StringToInt64()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("9876543210"),
            DataValue.FromString("Int64")
        ]);
        Assert.Equal(9876543210L, result.AsInt64());
    }

    [Fact]
    public void Cast_Int64ToString()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt64(9876543210L),
            DataValue.FromString("String")
        ]);
        Assert.Equal("9876543210", result.AsString());
    }

    [Fact]
    public void Cast_StringToFloat64()
    {
        DataValue result = _function.Execute([
            DataValue.FromString("3.141592653589793"),
            DataValue.FromString("Float64")
        ]);
        Assert.Equal(3.141592653589793, result.AsFloat64(), 14);
    }

    // ─────────────── cast(x, TypeLiteral) ───────────────

    [Fact]
    public void Cast_WithTypeLiteral_Works()
    {
        DataValue result = _function.Execute([
            DataValue.FromInt32(42),
            DataValue.FromType(DataKind.Float64)
        ]);
        Assert.Equal(DataKind.Float64, result.Kind);
        Assert.Equal(42.0, result.AsFloat64());
    }

    [Fact]
    public void ValidateArguments_AcceptsTypeLiteral()
    {
        DataKind result = _function.ValidateArguments([DataKind.Int32, DataKind.Type]);
        Assert.Equal(DataKind.String, result); // Placeholder — runtime determines actual kind.
    }

    // ─────────────── FormatNumericAsString (all numeric types) ───────────────

    [Theory]
    [InlineData(DataKind.UInt8, "42")]
    [InlineData(DataKind.Int8, "-7")]
    [InlineData(DataKind.Int16, "-1234")]
    [InlineData(DataKind.UInt16, "5678")]
    [InlineData(DataKind.Int32, "-100000")]
    [InlineData(DataKind.UInt32, "200000")]
    [InlineData(DataKind.Int64, "-9000000000")]
    [InlineData(DataKind.UInt64, "18000000000")]
    [InlineData(DataKind.Float32, "3.14")]
    [InlineData(DataKind.Float64, "2.718281828")]
    public void Cast_NumericToString_FormatsCorrectly(DataKind kind, string expected)
    {
        DataValue input = kind switch
        {
            DataKind.UInt8 => DataValue.FromUInt8(42),
            DataKind.Int8 => DataValue.FromInt8(-7),
            DataKind.Int16 => DataValue.FromInt16(-1234),
            DataKind.UInt16 => DataValue.FromUInt16(5678),
            DataKind.Int32 => DataValue.FromInt32(-100000),
            DataKind.UInt32 => DataValue.FromUInt32(200000),
            DataKind.Int64 => DataValue.FromInt64(-9000000000L),
            DataKind.UInt64 => DataValue.FromUInt64(18000000000UL),
            DataKind.Float32 => DataValue.FromFloat32(3.14f),
            DataKind.Float64 => DataValue.FromFloat64(2.718281828),
            _ => throw new ArgumentException($"Unexpected kind: {kind}"),
        };

        DataValue result = _function.Execute([input, DataValue.FromString("String")]);

        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal(expected, result.AsString());
    }

    [Fact]
    public void Cast_BooleanToString()
    {
        DataValue trueResult = _function.Execute([DataValue.FromBoolean(true), DataValue.FromString("String")]);
        DataValue falseResult = _function.Execute([DataValue.FromBoolean(false), DataValue.FromString("String")]);

        Assert.Equal("true", trueResult.AsString());
        Assert.Equal("false", falseResult.AsString());
    }

    // ─────────────── ParseStringToNumeric (all numeric types) ───────────────

    [Theory]
    [InlineData("42", DataKind.UInt8)]
    [InlineData("-7", DataKind.Int8)]
    [InlineData("-1234", DataKind.Int16)]
    [InlineData("5678", DataKind.UInt16)]
    [InlineData("-100000", DataKind.Int32)]
    [InlineData("200000", DataKind.UInt32)]
    [InlineData("-9000000000", DataKind.Int64)]
    [InlineData("18000000000", DataKind.UInt64)]
    [InlineData("3.14", DataKind.Float32)]
    [InlineData("2.718281828", DataKind.Float64)]
    public void Cast_StringToNumeric_ParsesCorrectly(string input, DataKind targetKind)
    {
        DataValue result = _function.Execute([
            DataValue.FromString(input),
            DataValue.FromString(targetKind.ToString())
        ]);

        Assert.Equal(targetKind, result.Kind);
        Assert.False(result.IsNull);
    }

    [Theory]
    [InlineData("abc", "Int32")]
    [InlineData("3.14", "Int32")]
    [InlineData("-1", "UInt8")]
    [InlineData("999", "Int8")]
    public void Cast_StringToNumeric_InvalidInput_Throws(string input, string targetKind)
    {
        Assert.ThrowsAny<Exception>(() =>
            _function.Execute([DataValue.FromString(input), DataValue.FromString(targetKind)]));
    }
}
