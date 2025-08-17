using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class ArithmeticFunctionTests
{
    [Fact]
    public void Abs_Scalar_Positive()
    {
        AbsFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromFloat32(5)]).AsFloat32());
    }

    [Fact]
    public void Abs_Scalar_Negative()
    {
        AbsFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromFloat32(-5)]).AsFloat32());
    }

    [Fact]
    public void Abs_Vector_PreservesShape()
    {
        AbsFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([-3f, 0f, 4f])]);
        float[] values = result.AsVector();
        Assert.Equal([3f, 0f, 4f], values);
    }

    [Fact]
    public void Abs_Null_ReturnsNull()
    {
        AbsFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Sign_Positive() => Assert.Equal(1f, new SignFunction().Execute([DataValue.FromFloat32(42)]).AsFloat32());

    [Fact]
    public void Sign_Negative() => Assert.Equal(-1f, new SignFunction().Execute([DataValue.FromFloat32(-7)]).AsFloat32());

    [Fact]
    public void Sign_Zero() => Assert.Equal(0f, new SignFunction().Execute([DataValue.FromFloat32(0)]).AsFloat32());

    [Fact]
    public void Negate_Scalar()
    {
        NegateFunction function = new();
        Assert.Equal(-3f, function.Execute([DataValue.FromFloat32(3)]).AsFloat32());
    }

    [Fact]
    public void Negate_NegativeBecomesPositive()
    {
        NegateFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromFloat32(-5)]).AsFloat32());
    }

    [Fact]
    public void Mod_BasicModulus()
    {
        ModFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(7), DataValue.FromFloat32(3)]);
        Assert.Equal(1f, result.AsFloat32());
    }

    [Fact]
    public void Add_Scalars()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(3), DataValue.FromFloat32(4)]);
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void Add_ScalarBroadcastToVector()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10), DataValue.FromVector([1f, 2f, 3f])]);
        Assert.Equal([11f, 12f, 13f], result.AsVector());
    }

    [Fact]
    public void Add_VectorAndScalar()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f]), DataValue.FromFloat32(10)]);
        Assert.Equal([11f, 12f, 13f], result.AsVector());
    }

    [Fact]
    public void Add_TwoVectors()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f]), DataValue.FromVector([4f, 5f, 6f])]);
        Assert.Equal([5f, 7f, 9f], result.AsVector());
    }

    [Fact]
    public void Subtract_Scalars()
    {
        SubtractFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10), DataValue.FromFloat32(3)]);
        Assert.Equal(7f, result.AsFloat32());
    }

    [Fact]
    public void Multiply_Scalars()
    {
        MultiplyFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(4), DataValue.FromFloat32(5)]);
        Assert.Equal(20f, result.AsFloat32());
    }

    [Fact]
    public void Divide_Scalars()
    {
        DivideFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(10), DataValue.FromFloat32(4)]);
        Assert.Equal(2.5f, result.AsFloat32());
    }

    [Fact]
    public void Divide_ByZero_ReturnsInfinity()
    {
        DivideFunction function = new();
        DataValue result = function.Execute([DataValue.FromFloat32(1), DataValue.FromFloat32(0)]);
        Assert.True(float.IsInfinity(result.AsFloat32()));
    }

    [Fact]
    public void Add_Null_ReturnsNull()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Float32), DataValue.FromFloat32(5)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Binary_Validate_WrongArgCount_Throws()
    {
        AddFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void Binary_Validate_UnsupportedKind_Throws()
    {
        AddFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Float32]));
    }

    [Fact]
    public void Add_Matrix_PreservesShape()
    {
        AddFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2),
            DataValue.FromFloat32(10)
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(2, columns);
        Assert.Equal([11f, 12f, 13f, 14f], data);
    }

    [Fact]
    public void Abs_UInt8_ReturnsScalar()
    {
        AbsFunction function = new();
        DataKind resultKind = function.ValidateArguments([DataKind.UInt8]);
        Assert.Equal(DataKind.Float32, resultKind);
    }
}
