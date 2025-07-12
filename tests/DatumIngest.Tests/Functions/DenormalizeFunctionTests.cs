using DatumQuery.Functions.Scalar;
using DatumQuery.Model;

namespace DatumQuery.Tests.Functions;

public class DenormalizeFunctionTests
{
    private readonly DenormalizeFunction _function = new();

    [Fact]
    public void Name_IsDenormalize()
    {
        Assert.Equal("denormalize", _function.Name);
    }

    [Fact]
    public void Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Validate_UnsupportedKind_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void Execute_Scalar_MultipliesByFactor()
    {
        DataValue result = _function.Execute([
            DataValue.FromScalar(0.5f),
            DataValue.FromScalar(255)
        ]);
        Assert.Equal(127.5f, result.AsScalar(), 0.0001f);
    }

    [Fact]
    public void Execute_Vector_MultipliesEachElement()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([0, 0.5f, 1]),
            DataValue.FromScalar(100)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(0f, vector[0]);
        Assert.Equal(50f, vector[1]);
        Assert.Equal(100f, vector[2]);
    }

    [Fact]
    public void Execute_Matrix_MultipliesAllElements()
    {
        DataValue result = _function.Execute([
            DataValue.FromMatrix([0.1f, 0.2f, 0.3f, 0.4f], 2, 2),
            DataValue.FromScalar(10)
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(2, columns);
        Assert.Equal(1f, data[0], 0.0001f);
        Assert.Equal(2f, data[1], 0.0001f);
        Assert.Equal(3f, data[2], 0.0001f);
        Assert.Equal(4f, data[3], 0.0001f);
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([
            DataValue.Null(DataKind.Scalar),
            DataValue.FromScalar(255)
        ]);
        Assert.True(result.IsNull);
    }
}
