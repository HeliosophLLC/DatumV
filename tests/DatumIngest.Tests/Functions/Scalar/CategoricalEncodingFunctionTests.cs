using DatumIngest.Functions.Scalar;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for categorical encoding functions: one_hot, one_hot_unk, label_encode, label_encode_unk, hash_encode.
/// </summary>
public class CategoricalEncodingFunctionTests
{
    // ── one_hot ────────────────────────────────────────────

    [Fact]
    public void OneHot_KnownValue_SetsCorrectIndex()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("dog"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(3, vector.Length);
        Assert.Equal([0f, 1f, 0f], vector);
    }

    [Fact]
    public void OneHot_FirstLabel_SetsIndexZero()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("cat"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal([1f, 0f, 0f], result.AsVector());
    }

    [Fact]
    public void OneHot_LastLabel_SetsLastIndex()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("bird"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal([0f, 0f, 1f], result.AsVector());
    }

    [Fact]
    public void OneHot_UnknownValue_ReturnsZeroVector()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("fish"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal([0f, 0f, 0f], result.AsVector());
    }

    [Fact]
    public void OneHot_NullInput_ReturnsNull()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("cat"),
            DataValue.FromString("dog")
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Vector, result.Kind);
    }

    [Fact]
    public void OneHot_SingleLabel_ProducesLengthOneVector()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("yes"),
            DataValue.FromString("yes")
        ]);
        Assert.Equal([1f], result.AsVector());
    }

    [Fact]
    public void OneHot_CaseSensitive()
    {
        OneHotFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("Cat"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog")
        ]);
        Assert.Equal([0f, 0f], result.AsVector());
    }

    [Fact]
    public void OneHot_Validate_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new OneHotFunction().ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void OneHot_Validate_NonString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new OneHotFunction().ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    [Fact]
    public void OneHot_Validate_ReturnsVector()
    {
        DataKind result = new OneHotFunction().ValidateArguments([DataKind.String, DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.Vector, result);
    }

    // ── one_hot_unk ────────────────────────────────────────

    [Fact]
    public void OneHotUnknown_KnownValue_SetsCorrectIndex()
    {
        OneHotUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("dog"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(4, vector.Length); // K+1
        Assert.Equal([0f, 1f, 0f, 0f], vector);
    }

    [Fact]
    public void OneHotUnknown_UnknownValue_ActivatesLastDimension()
    {
        OneHotUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("fish"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(4, vector.Length);
        Assert.Equal([0f, 0f, 0f, 1f], vector);
    }

    [Fact]
    public void OneHotUnknown_NullInput_ReturnsNull()
    {
        OneHotUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("cat"),
            DataValue.FromString("dog")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void OneHotUnknown_SingleLabel_UnknownUsesSecondDimension()
    {
        OneHotUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("no"),
            DataValue.FromString("yes")
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(2, vector.Length);
        Assert.Equal([0f, 1f], vector);
    }

    // ── label_encode ────────────────────────────────────────

    [Fact]
    public void LabelEncode_KnownValue_ReturnsIndex()
    {
        LabelEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("dog"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void LabelEncode_FirstLabel_ReturnsZero()
    {
        LabelEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("cat"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void LabelEncode_LastLabel_ReturnsLastIndex()
    {
        LabelEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("bird"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void LabelEncode_UnknownValue_ReturnsNegativeOne()
    {
        LabelEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("fish"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(-1f, result.AsScalar());
    }

    [Fact]
    public void LabelEncode_NullInput_ReturnsNull()
    {
        LabelEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("cat"),
            DataValue.FromString("dog")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void LabelEncode_Validate_ReturnsScalar()
    {
        DataKind result = new LabelEncodeFunction().ValidateArguments([DataKind.String, DataKind.String]);
        Assert.Equal(DataKind.Scalar, result);
    }

    // ── label_encode_unk ────────────────────────────────────

    [Fact]
    public void LabelEncodeUnknown_KnownValue_ReturnsIndex()
    {
        LabelEncodeUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("dog"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(1f, result.AsScalar());
    }

    [Fact]
    public void LabelEncodeUnknown_UnknownValue_ReturnsDomainSize()
    {
        LabelEncodeUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("fish"),
            DataValue.FromString("cat"),
            DataValue.FromString("dog"),
            DataValue.FromString("bird")
        ]);
        Assert.Equal(3f, result.AsScalar()); // K = 3
    }

    [Fact]
    public void LabelEncodeUnknown_NullInput_ReturnsNull()
    {
        LabelEncodeUnknownFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromString("cat"),
            DataValue.FromString("dog")
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void LabelEncodeUnknown_Validate_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new LabelEncodeUnknownFunction().ValidateArguments([DataKind.String]));
    }

    // ── hash_encode ────────────────────────────────────────

    [Fact]
    public void HashEncode_ProducesCorrectDimension()
    {
        HashEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromScalar(64)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(64, vector.Length);
    }

    [Fact]
    public void HashEncode_ProducesExactlyOneHot()
    {
        HashEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromScalar(128)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(1f, vector.Sum());
        Assert.Single(vector, v => v == 1f);
    }

    [Fact]
    public void HashEncode_NullInput_ReturnsNull()
    {
        HashEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromScalar(32)
        ]);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Vector, result.Kind);
    }

    [Fact]
    public void HashEncode_Deterministic()
    {
        HashEncodeFunction function = new();
        DataValue result1 = function.Execute([DataValue.FromString("test"), DataValue.FromScalar(256)]);
        DataValue result2 = function.Execute([DataValue.FromString("test"), DataValue.FromScalar(256)]);
        Assert.Equal(result1.AsVector(), result2.AsVector());
    }

    [Fact]
    public void HashEncode_DifferentValues_MayDifferentBuckets()
    {
        HashEncodeFunction function = new();
        float[] vectorA = function.Execute([DataValue.FromString("cat"), DataValue.FromScalar(1024)]).AsVector();
        float[] vectorB = function.Execute([DataValue.FromString("dog"), DataValue.FromScalar(1024)]).AsVector();

        int indexA = Array.IndexOf(vectorA, 1f);
        int indexB = Array.IndexOf(vectorB, 1f);
        // Different strings should hash to different buckets (extremely likely with 1024 buckets)
        Assert.NotEqual(indexA, indexB);
    }

    [Fact]
    public void HashEncode_UInt8BucketCount()
    {
        HashEncodeFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("value"),
            DataValue.FromUInt8(16)
        ]);
        float[] vector = result.AsVector();
        Assert.Equal(16, vector.Length);
        Assert.Equal(1f, vector.Sum());
    }

    [Fact]
    public void HashEncode_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HashEncodeFunction().ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void HashEncode_Validate_NonStringFirstArg_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HashEncodeFunction().ValidateArguments([DataKind.Scalar, DataKind.Scalar]));
    }

    [Fact]
    public void HashEncode_Validate_NonNumericSecondArg_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new HashEncodeFunction().ValidateArguments([DataKind.String, DataKind.String]));
    }

    [Fact]
    public void HashEncode_Validate_ReturnsVector()
    {
        DataKind result = new HashEncodeFunction().ValidateArguments([DataKind.String, DataKind.Scalar]);
        Assert.Equal(DataKind.Vector, result);
    }
}
