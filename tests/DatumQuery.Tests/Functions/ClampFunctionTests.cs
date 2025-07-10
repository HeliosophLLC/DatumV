using Axon.QueryEngine.Functions.Scalar;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Functions;

public class ClampFunctionTests
{
    private readonly ClampFunction _function = new();

    [Fact]
    public void Name_IsClamp()
    {
        Assert.Equal("clamp", _function.Name);
    }

    [Fact]
    public void Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar]));
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void Validate_UnsupportedKind_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.String, DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void Execute_Scalar_ClampsToRange()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(150),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal(100f, result.AsScalar());
    }

    [Fact]
    public void Execute_Scalar_BelowMin()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(-5),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void Execute_Scalar_WithinRange_Unchanged()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(50),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal(50f, result.AsScalar());
    }

    [Fact]
    public void Execute_Vector_ClampsEachElement()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([-1, 50, 200]),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(0f, vector[0]);
        Assert.Equal(50f, vector[1]);
        Assert.Equal(100f, vector[2]);
    }

    [Fact]
    public void Execute_Matrix_ClampsAllElements()
    {
        DataValue result = _function.Execute([
            DataValue.FromMatrix([-1, 50, 200, 75], 2, 2),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(2, columns);
        Assert.Equal(0f, data[0]);
        Assert.Equal(50f, data[1]);
        Assert.Equal(100f, data[2]);
        Assert.Equal(75f, data[3]);
    }

    [Fact]
    public void Execute_Tensor_ClampsAllElements()
    {
        DataValue result = _function.Execute([
            DataValue.FromTensor([-10, 500], [2]),
            DataValue.FromScalar(0),
            DataValue.FromScalar(255)
        ]);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([2], shape);
        Assert.Equal(0f, data[0]);
        Assert.Equal(255f, data[1]);
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(0),
            DataValue.FromScalar(100)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }
}
