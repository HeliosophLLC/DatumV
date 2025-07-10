using Axon.QueryEngine.Functions.Math;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Functions.Math;

public class TensorIntrospectionFunctionTests
{
    // ─────────────────────────── rank() ───────────────────────────

    [Fact]
    public void Rank_Vector_ReturnsOne()
    {
        RankFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f])]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void Rank_Matrix_ReturnsTwo()
    {
        RankFunction function = new();
        DataValue result = function.Execute([DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3)]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void Rank_Tensor_ReturnsShapeLength()
    {
        RankFunction function = new();
        DataValue result = function.Execute([DataValue.FromTensor([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], [2, 2, 2])]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Rank_NullVector_ReturnsNull()
    {
        RankFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Vector)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void Rank_InvalidKind_ThrowsOnValidation()
    {
        RankFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void Rank_WrongArgumentCount_ThrowsOnValidation()
    {
        RankFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Vector, DataKind.Scalar]));
    }

    // ─────────────────────────── rdim() ───────────────────────────

    [Fact]
    public void Rdim_VectorAxis0_ReturnsLength()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f, 4f, 5f]),
            DataValue.FromScalar(0)
        ]);
        Assert.Equal(5f, result.AsScalar());
    }

    [Fact]
    public void Rdim_MatrixAxis0_ReturnsRows()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3),
            DataValue.FromScalar(0)
        ]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void Rdim_MatrixAxis1_ReturnsColumns()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3),
            DataValue.FromScalar(1)
        ]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Rdim_TensorAxis2_ReturnsThirdDimension()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromTensor(new float[24], [2, 3, 4]),
            DataValue.FromScalar(2)
        ]);
        Assert.Equal(4f, result.AsScalar());
    }

    [Fact]
    public void Rdim_VectorOutOfRange_Throws()
    {
        RdimFunction function = new();
        Assert.Throws<InvalidOperationException>(() => function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.FromScalar(1)
        ]));
    }

    [Fact]
    public void Rdim_MatrixOutOfRange_Throws()
    {
        RdimFunction function = new();
        Assert.Throws<InvalidOperationException>(() => function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2),
            DataValue.FromScalar(2)
        ]));
    }

    [Fact]
    public void Rdim_TensorOutOfRange_Throws()
    {
        RdimFunction function = new();
        Assert.Throws<InvalidOperationException>(() => function.Execute([
            DataValue.FromTensor(new float[8], [2, 2, 2]),
            DataValue.FromScalar(3)
        ]));
    }

    [Fact]
    public void Rdim_NullInput_ReturnsNull()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.Matrix),
            DataValue.FromScalar(0)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Scalar, result.Kind);
    }

    [Fact]
    public void Rdim_UInt8Axis_Works()
    {
        RdimFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3),
            DataValue.FromUInt8(1)
        ]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Rdim_WrongArgumentCount_ThrowsOnValidation()
    {
        RdimFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Vector]));
    }

    [Fact]
    public void Rdim_InvalidFirstArgument_ThrowsOnValidation()
    {
        RdimFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    // ─────────────────────────── shape() ──────────────────────────

    [Fact]
    public void Shape_Vector_ReturnsSingleElementVector()
    {
        ShapeFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f, 4f, 5f])]);
        Assert.Equal([5f], result.AsVector());
    }

    [Fact]
    public void Shape_Matrix_ReturnsRowsAndColumns()
    {
        ShapeFunction function = new();
        DataValue result = function.Execute([DataValue.FromMatrix([1f, 2f, 3f, 4f, 5f, 6f], 2, 3)]);
        Assert.Equal([2f, 3f], result.AsVector());
    }

    [Fact]
    public void Shape_Tensor_ReturnsAllDimensions()
    {
        ShapeFunction function = new();
        DataValue result = function.Execute([DataValue.FromTensor(new float[24], [2, 3, 4])]);
        Assert.Equal([2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Shape_NullTensor_ReturnsNull()
    {
        ShapeFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Tensor)]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Vector, result.Kind);
    }

    [Fact]
    public void Shape_InvalidKind_ThrowsOnValidation()
    {
        ShapeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Shape_WrongArgumentCount_ThrowsOnValidation()
    {
        ShapeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.ValidateArguments([]));
    }
}
