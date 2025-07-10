using DatumQuery.Functions.Math;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions.Math;

public class ArithmeticFunctionTests
{
    [Fact]
    public void Abs_Scalar_Positive()
    {
        AbsFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromScalar(5)]).AsScalar());
    }

    [Fact]
    public void Abs_Scalar_Negative()
    {
        AbsFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromScalar(-5)]).AsScalar());
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
        DataValue result = function.Execute([DataValue.Null(DataKind.Scalar)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Sign_Positive() => Assert.Equal(1f, new SignFunction().Execute([DataValue.FromScalar(42)]).AsScalar());

    [Fact]
    public void Sign_Negative() => Assert.Equal(-1f, new SignFunction().Execute([DataValue.FromScalar(-7)]).AsScalar());

    [Fact]
    public void Sign_Zero() => Assert.Equal(0f, new SignFunction().Execute([DataValue.FromScalar(0)]).AsScalar());

    [Fact]
    public void Negate_Scalar()
    {
        NegateFunction function = new();
        Assert.Equal(-3f, function.Execute([DataValue.FromScalar(3)]).AsScalar());
    }

    [Fact]
    public void Negate_NegativeBecomesPositive()
    {
        NegateFunction function = new();
        Assert.Equal(5f, function.Execute([DataValue.FromScalar(-5)]).AsScalar());
    }

    [Fact]
    public void Mod_BasicModulus()
    {
        ModFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(7), DataValue.FromScalar(3)]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void Add_Scalars()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(3), DataValue.FromScalar(4)]);
        Assert.Equal(7f, result.AsScalar());
    }

    [Fact]
    public void Add_ScalarBroadcastToVector()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(10), DataValue.FromVector([1f, 2f, 3f])]);
        Assert.Equal([11f, 12f, 13f], result.AsVector());
    }

    [Fact]
    public void Add_VectorAndScalar()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f]), DataValue.FromScalar(10)]);
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
        DataValue result = function.Execute([DataValue.FromScalar(10), DataValue.FromScalar(3)]);
        Assert.Equal(7f, result.AsScalar());
    }

    [Fact]
    public void Multiply_Scalars()
    {
        MultiplyFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(4), DataValue.FromScalar(5)]);
        Assert.Equal(20f, result.AsScalar());
    }

    [Fact]
    public void Divide_Scalars()
    {
        DivideFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(10), DataValue.FromScalar(4)]);
        Assert.Equal(2.5f, result.AsScalar());
    }

    [Fact]
    public void Divide_ByZero_ReturnsInfinity()
    {
        DivideFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(1), DataValue.FromScalar(0)]);
        Assert.True(float.IsInfinity(result.AsScalar()));
    }

    [Fact]
    public void Add_Null_ReturnsNull()
    {
        AddFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Scalar), DataValue.FromScalar(5)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Binary_Validate_WrongArgCount_Throws()
    {
        AddFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Binary_Validate_UnsupportedKind_Throws()
    {
        AddFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void Add_Matrix_PreservesShape()
    {
        AddFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2),
            DataValue.FromScalar(10)
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
        Assert.Equal(DataKind.Scalar, resultKind);
    }
}
