using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class ReductionFunctionTests
{
    [Fact]
    public void VecSum_Vector()
    {
        Assert.Equal(10f, new VecSumFunction().Execute([DataValue.FromVector([1f, 2f, 3f, 4f])]).AsFloat32());
    }

    [Fact]
    public void VecSum_Null_ReturnsNull()
    {
        Assert.True(new VecSumFunction().Execute([DataValue.Null(DataKind.Vector)]).IsNull);
    }

    [Fact]
    public void VecMean_Vector()
    {
        Assert.Equal(2.5f, new VecMeanFunction().Execute([DataValue.FromVector([1f, 2f, 3f, 4f])]).AsFloat32());
    }

    [Fact]
    public void VecMin_Vector()
    {
        Assert.Equal(1f, new VecMinFunction().Execute([DataValue.FromVector([3f, 1f, 4f, 2f])]).AsFloat32());
    }

    [Fact]
    public void VecMax_Vector()
    {
        Assert.Equal(4f, new VecMaxFunction().Execute([DataValue.FromVector([3f, 1f, 4f, 2f])]).AsFloat32());
    }

    [Fact]
    public void VecStd_Uniform()
    {
        Assert.Equal(0f, new VecStdFunction().Execute([DataValue.FromVector([5f, 5f, 5f])]).AsFloat32(), 1e-5f);
    }

    [Fact]
    public void VecStd_KnownValues()
    {
        // population std of [2, 4, 4, 4, 5, 5, 7, 9] = 2.0
        float result = new VecStdFunction().Execute([DataValue.FromVector([2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f])]).AsFloat32();
        Assert.Equal(2f, result, 1e-4f);
    }

    [Fact]
    public void VecVar_KnownValues()
    {
        float result = new VecVarFunction().Execute([DataValue.FromVector([2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f])]).AsFloat32();
        Assert.Equal(4f, result, 1e-4f);
    }

    [Fact]
    public void VecMedian_Odd()
    {
        Assert.Equal(3f, new VecMedianFunction().Execute([DataValue.FromVector([1f, 3f, 5f])]).AsFloat32());
    }

    [Fact]
    public void VecMedian_Even()
    {
        Assert.Equal(2.5f, new VecMedianFunction().Execute([DataValue.FromVector([1f, 2f, 3f, 4f])]).AsFloat32());
    }

    [Fact]
    public void VecArgmin_Vector()
    {
        Assert.Equal(1f, new VecArgminFunction().Execute([DataValue.FromVector([5f, 1f, 3f])]).AsFloat32());
    }

    [Fact]
    public void VecArgmax_Vector()
    {
        Assert.Equal(2f, new VecArgmaxFunction().Execute([DataValue.FromVector([5f, 1f, 9f])]).AsFloat32());
    }

    [Fact]
    public void VecNorm_L2()
    {
        float result = new VecNormFunction().Execute([DataValue.FromVector([3f, 4f])]).AsFloat32();
        Assert.Equal(5f, result, 1e-5f);
    }

    [Fact]
    public void VecNorm_L1()
    {
        float result = new VecNormFunction().Execute([DataValue.FromVector([-3f, 4f]), DataValue.FromFloat32(1)]).AsFloat32();
        Assert.Equal(7f, result, 1e-5f);
    }

    [Fact]
    public void VecNorm_LInfinity()
    {
        float result = new VecNormFunction().Execute([
            DataValue.FromVector([-3f, 4f, -5f]),
            DataValue.FromFloat32(float.PositiveInfinity)
        ]).AsFloat32();
        Assert.Equal(5f, result, 1e-5f);
    }

    [Fact]
    public void VecCountNonzero_Mixed()
    {
        Assert.Equal(2f, new VecCountNonzeroFunction().Execute([DataValue.FromVector([0f, 1f, 0f, 3f])]).AsFloat32());
    }

    [Fact]
    public void VecAny_HasNonzero()
    {
        Assert.True(new VecAnyFunction().Execute([DataValue.FromVector([0f, 0f, 1f])]).AsBoolean());
    }

    [Fact]
    public void VecAny_AllZero()
    {
        Assert.False(new VecAnyFunction().Execute([DataValue.FromVector([0f, 0f, 0f])]).AsBoolean());
    }

    [Fact]
    public void VecAll_AllNonzero()
    {
        Assert.True(new VecAllFunction().Execute([DataValue.FromVector([1f, 2f, 3f])]).AsBoolean());
    }

    [Fact]
    public void VecAll_HasZero()
    {
        Assert.False(new VecAllFunction().Execute([DataValue.FromVector([1f, 0f, 3f])]).AsBoolean());
    }

    [Fact]
    public void VecProduct_Vector()
    {
        Assert.Equal(24f, new VecProductFunction().Execute([DataValue.FromVector([2f, 3f, 4f])]).AsFloat32());
    }

    [Fact]
    public void VecSum_Matrix()
    {
        Assert.Equal(10f, new VecSumFunction().Execute([DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2)]).AsFloat32());
    }

    [Fact]
    public void VecSum_Validate_Scalar_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VecSumFunction().ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void VecSum_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VecSumFunction().ValidateArguments([DataKind.Vector, DataKind.Vector]));
    }

    [Fact]
    public void VecMedian_Null_ReturnsNull()
    {
        Assert.True(new VecMedianFunction().Execute([DataValue.Null(DataKind.Vector)]).IsNull);
    }
}
