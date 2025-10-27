using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class ClampFunctionTests : ServiceTestBase
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
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32]));
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Validate_UnsupportedKind_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.String, DataKind.Float32, DataKind.Float32]));
    }

    [Fact]
    public void Execute_Scalar_ClampsToRange()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(150),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.Equal(100f, result.AsFloat32());
    }

    [Fact]
    public void Execute_Scalar_BelowMin()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(-5),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void Execute_Scalar_WithinRange_Unchanged()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(50),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.Equal(50f, result.AsFloat32());
    }

    [Fact]
    public void Execute_Vector_ClampsEachElement()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([-1, 50, 200]),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
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
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
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
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(255)
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
            DataValue.Null(DataKind.Float32),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }
}
