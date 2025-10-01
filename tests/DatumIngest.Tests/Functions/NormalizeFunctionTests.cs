using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class NormalizeFunctionTests
{
    private readonly MinMaxNormalizeFunction _function = new();

    [Fact]
    public void Name_IsMinMaxNormalize()
    {
        Assert.Equal("min_max_normalize", _function.Name);
    }

    [Fact]
    public void Validate_UInt8_ReturnsScalar()
    {
        DataKind result = _function.ValidateArguments([DataKind.UInt8]);
        Assert.Equal(DataKind.Float32, result);
    }

    [Fact]
    public void Validate_UInt8Array_ReturnsVector()
    {
        DataKind result = _function.ValidateArguments([DataKind.UInt8Array]);
        Assert.Equal(DataKind.Vector, result);
    }

    [Fact]
    public void Validate_VectorWithoutMinMax_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.Vector]));
    }

    [Fact]
    public void Validate_VectorWithMinMax_ReturnsVector()
    {
        DataKind result = _function.ValidateArguments([DataKind.Vector, DataKind.Float32, DataKind.Float32]);
        Assert.Equal(DataKind.Vector, result);
    }

    [Fact]
    public void Validate_UnsupportedKind_Throws()
    {
        Assert.Throws<ArgumentException>(() => _function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void Execute_UInt8_NormalizesTo0_1()
    {
        DataValue result = _function.Execute([DataValue.FromUInt8(255)]);
        Assert.Equal(1.0f, result.AsFloat32());

        DataValue result2 = _function.Execute([DataValue.FromUInt8(0)]);
        Assert.Equal(0.0f, result2.AsFloat32());

        DataValue result3 = _function.Execute([DataValue.FromUInt8(128)]);
        Assert.Equal(128.0f / 255.0f, result3.AsFloat32(), 0.0001f);
    }

    [Fact]
    public void Execute_UInt8Array_NormalizesEachByte()
    {
        DataValue result = _function.Execute([DataValue.FromUInt8Array([0, 128, 255])]);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal(0.0f, vector[0]);
        Assert.Equal(128.0f / 255.0f, vector[1], 0.0001f);
        Assert.Equal(1.0f, vector[2]);
    }

    [Fact]
    public void Execute_Scalar_NormalizesWithMinMax()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(50),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        Assert.Equal(0.5f, result.AsFloat32(), 0.0001f);
    }

    [Fact]
    public void Execute_Scalar_ZeroRange_ReturnsZero()
    {
        DataValue result = _function.Execute([
            DataValue.FromFloat32(50),
            DataValue.FromFloat32(50),
            DataValue.FromFloat32(50)
        ]);
        Assert.Equal(0.0f, result.AsFloat32());
    }

    [Fact]
    public void Execute_Vector_NormalizesEachElement()
    {
        DataValue result = _function.Execute([
            DataValue.FromVector([10, 20, 30]),
            DataValue.FromFloat32(10),
            DataValue.FromFloat32(30)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(0.0f, vector[0], 0.0001f);
        Assert.Equal(0.5f, vector[1], 0.0001f);
        Assert.Equal(1.0f, vector[2], 0.0001f);
    }

    [Fact]
    public void Execute_NullInput_ReturnsNull()
    {
        DataValue result = _function.Execute([DataValue.Null(DataKind.UInt8)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Execute_Matrix_NormalizesAllElements()
    {
        DataValue result = _function.Execute([
            DataValue.FromMatrix([0, 50, 100, 200], 2, 2),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(200)
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(2, columns);
        Assert.Equal(0.0f, data[0], 0.0001f);
        Assert.Equal(0.25f, data[1], 0.0001f);
        Assert.Equal(0.5f, data[2], 0.0001f);
        Assert.Equal(1.0f, data[3], 0.0001f);
    }

    [Fact]
    public void Execute_Tensor_NormalizesAllElements()
    {
        DataValue result = _function.Execute([
            DataValue.FromTensor([0, 100], [2]),
            DataValue.FromFloat32(0),
            DataValue.FromFloat32(100)
        ]);
        float[] data = result.AsTensor(out int[] shape);
        Assert.Equal([2], shape);
        Assert.Equal(0.0f, data[0], 0.0001f);
        Assert.Equal(1.0f, data[1], 0.0001f);
    }
}
