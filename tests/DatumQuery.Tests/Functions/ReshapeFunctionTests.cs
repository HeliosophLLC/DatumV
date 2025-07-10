using DatumQuery.Functions.Scalar;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions;

public class ReshapeFunctionTests
{
    private readonly ReshapeFunction _function = new();

    [Fact]
    public void Name_IsReshape()
    {
        Assert.Equal("reshape", _function.Name);
    }

    [Fact]
    public void Validate_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Vector]));
    }

    [Fact]
    public void Validate_UnsupportedKind_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void Validate_OneDimension_ReturnsVector()
    {
        DataKind result = _function.ValidateArguments([DataKind.Matrix, DataKind.Scalar]);
        Assert.Equal(DataKind.Vector, result);
    }

    [Fact]
    public void Validate_TwoDimensions_ReturnsMatrix()
    {
        DataKind result = _function.ValidateArguments([DataKind.Vector, DataKind.Scalar, DataKind.Scalar]);
        Assert.Equal(DataKind.Matrix, result);
    }

    [Fact]
    public void Validate_ThreeDimensions_ReturnsTensor()
    {
        DataKind result = _function.ValidateArguments([DataKind.Vector, DataKind.Scalar, DataKind.Scalar, DataKind.Scalar]);
        Assert.Equal(DataKind.Tensor, result);
    }

    [Fact]
    public void Execute_VectorToMatrix()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([1, 2, 3, 4, 5, 6]),
            DataValue.FromScalar(2),
            DataValue.FromScalar(3)
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(3, columns);
        Assert.Equal([1, 2, 3, 4, 5, 6], data);
    }

    [Fact]
    public void Execute_MatrixToVector()
    {
        DataValue result = _function.Execute([
            DataValue.FromMatrix([1, 2, 3, 4], 2, 2),
            DataValue.FromScalar(4)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal([1, 2, 3, 4], vector);
    }

    [Fact]
    public void Execute_VectorToTensor()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([1, 2, 3, 4, 5, 6, 7, 8]),
            DataValue.FromScalar(2),
            DataValue.FromScalar(2),
            DataValue.FromScalar(2)
        ]);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([2, 2, 2], shape);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], data);
    }

    [Fact]
    public void Execute_MismatchedElementCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.Execute([
            DataValue.FromVector([1, 2, 3]),
            DataValue.FromScalar(2),
            DataValue.FromScalar(2)
        ]));
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.Null(DataKind.Vector),
            DataValue.FromScalar(3)
        ]);
        Assert.True(result.IsNull);
    }
}
