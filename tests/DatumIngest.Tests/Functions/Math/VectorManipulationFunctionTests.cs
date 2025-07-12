using DatumIngest.Functions.Math;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Math;

public class VectorManipulationFunctionTests
{
    [Fact]
    public void VecSlice_Middle()
    {
        VecSliceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([10f, 20f, 30f, 40f, 50f]),
            DataValue.FromScalar(1),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal([20f, 30f, 40f], result.AsVector());
    }

    [Fact]
    public void VecSlice_OutOfBounds_Clamps()
    {
        VecSliceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([10f, 20f, 30f]),
            DataValue.FromScalar(1),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal([20f, 30f], result.AsVector());
    }

    [Fact]
    public void VecConcat_TwoVectors()
    {
        VecConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.FromVector([3f, 4f])
        ]);
        Assert.Equal([1f, 2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void VecConcat_ThreeVectors()
    {
        VecConcatFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f]),
            DataValue.FromVector([2f]),
            DataValue.FromVector([3f])
        ]);
        Assert.Equal([1f, 2f, 3f], result.AsVector());
    }

    [Fact]
    public void VecReverse_Vector()
    {
        VecReverseFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f])]);
        Assert.Equal([3f, 2f, 1f], result.AsVector());
    }

    [Fact]
    public void VecSort_Unsorted()
    {
        VecSortFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([3f, 1f, 4f, 1f, 5f])]);
        Assert.Equal([1f, 1f, 3f, 4f, 5f], result.AsVector());
    }

    [Fact]
    public void VecUnique_RemovesDuplicates()
    {
        VecUniqueFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 2f, 3f, 1f])]);
        Assert.Equal([1f, 2f, 3f], result.AsVector());
    }

    [Fact]
    public void VecFlatten_Matrix()
    {
        VecFlattenFunction function = new();
        DataValue result = function.Execute([DataValue.FromMatrix([1f, 2f, 3f, 4f], 2, 2)]);
        Assert.Equal(DataKind.Vector, result.Kind);
        Assert.Equal([1f, 2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void VecFlatten_Vector_ReturnsVector()
    {
        VecFlattenFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f, 3f])]);
        Assert.Equal([1f, 2f, 3f], result.AsVector());
    }

    [Fact]
    public void VecPad_ShorterThanTarget()
    {
        VecPadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.FromScalar(5),
            DataValue.FromScalar(0)
        ]);
        Assert.Equal([1f, 2f, 0f, 0f, 0f], result.AsVector());
    }

    [Fact]
    public void VecPad_AlreadyLongEnough()
    {
        VecPadFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromScalar(2),
            DataValue.FromScalar(0)
        ]);
        Assert.Equal([1f, 2f, 3f], result.AsVector());
    }

    [Fact]
    public void VecRepeat_Twice()
    {
        VecRepeatFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f]), DataValue.FromScalar(3)]);
        Assert.Equal([1f, 2f, 1f, 2f, 1f, 2f], result.AsVector());
    }

    [Fact]
    public void Linspace_FivePoints()
    {
        LinspaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(0),
            DataValue.FromScalar(1),
            DataValue.FromScalar(5)
        ]);
        float[] values = result.AsVector();
        Assert.Equal(5, values.Length);
        Assert.Equal(0f, values[0], 1e-5f);
        Assert.Equal(0.25f, values[1], 1e-5f);
        Assert.Equal(0.5f, values[2], 1e-5f);
        Assert.Equal(0.75f, values[3], 1e-5f);
        Assert.Equal(1f, values[4], 1e-5f);
    }

    [Fact]
    public void Linspace_SinglePoint()
    {
        LinspaceFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(5),
            DataValue.FromScalar(10),
            DataValue.FromScalar(1)
        ]);
        Assert.Equal([5f], result.AsVector());
    }

    [Fact]
    public void Arange_Basic()
    {
        ArangeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(0),
            DataValue.FromScalar(5),
            DataValue.FromScalar(1)
        ]);
        Assert.Equal([0f, 1f, 2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Arange_Step2()
    {
        ArangeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(0),
            DataValue.FromScalar(10),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal([0f, 3f, 6f, 9f], result.AsVector());
    }

    [Fact]
    public void Arange_Negative()
    {
        ArangeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(5),
            DataValue.FromScalar(0),
            DataValue.FromScalar(-1)
        ]);
        Assert.Equal([5f, 4f, 3f, 2f, 1f], result.AsVector());
    }

    [Fact]
    public void Arange_ZeroStep_Throws()
    {
        ArangeFunction function = new();
        Assert.Throws<ArgumentException>(() => function.Execute([
            DataValue.FromScalar(0),
            DataValue.FromScalar(5),
            DataValue.FromScalar(0)
        ]));
    }

    [Fact]
    public void VecSlice_Null_ReturnsNull()
    {
        VecSliceFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.Vector), DataValue.FromScalar(0), DataValue.FromScalar(1)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void VecConcat_Null_ReturnsNull()
    {
        VecConcatFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f]), DataValue.Null(DataKind.Vector)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void VecConcat_Validate_LessThan2_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VecConcatFunction().ValidateArguments([DataKind.Vector]));
    }

    [Fact]
    public void Vec_AllScalars()
    {
        VecFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(1),
            DataValue.FromScalar(2),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal([1f, 2f, 3f], result.AsVector());
    }

    [Fact]
    public void Vec_AllVectors()
    {
        VecFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.FromVector([3f, 4f])
        ]);
        Assert.Equal([1f, 2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Vec_MixedScalarsAndVectors()
    {
        VecFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromScalar(1),
            DataValue.FromVector([2f, 3f]),
            DataValue.FromScalar(4)
        ]);
        Assert.Equal([1f, 2f, 3f, 4f], result.AsVector());
    }

    [Fact]
    public void Vec_SingleScalar()
    {
        VecFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(5)]);
        Assert.Equal([5f], result.AsVector());
    }

    [Fact]
    public void Vec_SingleVector()
    {
        VecFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1f, 2f])]);
        Assert.Equal([1f, 2f], result.AsVector());
    }

    [Fact]
    public void Vec_Null_ReturnsNull()
    {
        VecFunction function = new();
        DataValue result = function.Execute([DataValue.FromScalar(1), DataValue.Null(DataKind.Vector)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Vec_Validate_NoArguments_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VecFunction().ValidateArguments([]));
    }

    [Fact]
    public void Vec_Validate_InvalidType_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VecFunction().ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void Tensor_TwoVectors()
    {
        TensorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromVector([4f, 5f, 6f])
        ]);
        Assert.Equal(DataKind.Matrix, result.Kind);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(2, rows);
        Assert.Equal(3, columns);
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], data);
    }

    [Fact]
    public void Tensor_ThreeVectors()
    {
        TensorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.FromVector([3f, 4f]),
            DataValue.FromVector([5f, 6f])
        ]);
        float[] data = result.AsMatrix(out int rows, out int columns);
        Assert.Equal(3, rows);
        Assert.Equal(2, columns);
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], data);
    }

    [Fact]
    public void Tensor_Null_ReturnsNull()
    {
        TensorFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromVector([1f, 2f]),
            DataValue.Null(DataKind.Vector)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Tensor_MismatchedLengths_Throws()
    {
        TensorFunction function = new();
        Assert.Throws<ArgumentException>(() => function.Execute([
            DataValue.FromVector([1f, 2f, 3f]),
            DataValue.FromVector([4f, 5f])
        ]));
    }

    [Fact]
    public void Tensor_Validate_SingleVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TensorFunction().ValidateArguments([DataKind.Vector]));
    }

    [Fact]
    public void Tensor_Validate_NonVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TensorFunction().ValidateArguments([DataKind.Scalar, DataKind.Vector]));
    }
}
