using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Functions.Scalar.Math;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Scalar;

public sealed class NumericFunctionTests
{
    // ----- sign -----

    [Fact]
    public void Sign_Metadata()
    {
        Assert.Equal("sign", SignFunction.Name);
        Assert.Equal(FunctionCategory.Numeric, SignFunction.Category);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 1)]
    [InlineData(-3, -1)]
    public void Sign_Int32_ReturnsMinusOneZeroOrOne(int input, int expected)
    {
        ValueRef result = Invoke<SignFunction>(ValueRef.FromInt32(input));
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(expected, result.AsInt32());
    }

    [Fact]
    public void Sign_Float64_HandlesNaN()
    {
        ValueRef result = Invoke<SignFunction>(ValueRef.FromFloat64(double.NaN));
        Assert.True(double.IsNaN(result.AsFloat64()));
    }

    [Fact]
    public void Sign_Float64_NegativeZeroReturnsZero()
    {
        ValueRef result = Invoke<SignFunction>(ValueRef.FromFloat64(-0.0));
        Assert.Equal(0.0, result.AsFloat64());
    }

    [Fact]
    public void Sign_UInt8_NonZeroReturnsOne()
    {
        ValueRef result = Invoke<SignFunction>(ValueRef.FromUInt8(42));
        Assert.Equal((byte)1, result.AsUInt8());
    }

    [Fact]
    public void Sign_NullPropagates()
    {
        ValueRef result = Invoke<SignFunction>(ValueRef.Null(DataKind.Int32));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    // ----- negate -----

    [Fact]
    public void Negate_Metadata()
    {
        Assert.Equal("negate", NegateFunction.Name);
    }

    [Fact]
    public void Negate_Int32_FlipsSign()
    {
        ValueRef result = Invoke<NegateFunction>(ValueRef.FromInt32(5));
        Assert.Equal(-5, result.AsInt32());
    }

    [Fact]
    public void Negate_Float64_FlipsSign()
    {
        ValueRef result = Invoke<NegateFunction>(ValueRef.FromFloat64(2.5));
        Assert.Equal(-2.5, result.AsFloat64());
    }

    [Fact]
    public void Negate_Int32MinValue_Overflows()
    {
        Assert.Throws<OverflowException>(
            () => Invoke<NegateFunction>(ValueRef.FromInt32(int.MinValue)));
    }

    [Fact]
    public void Negate_UInt32_Rejected()
    {
        Assert.Throws<FunctionArgumentException>(
            () => new NegateFunction().ValidateArguments([DataKind.UInt32]));
    }

    [Fact]
    public void Negate_NullPropagates()
    {
        ValueRef result = Invoke<NegateFunction>(ValueRef.Null(DataKind.Float64));
        Assert.True(result.IsNull);
    }

    // ----- cbrt -----

    [Fact]
    public void Cbrt_Metadata()
    {
        Assert.Equal("cbrt", CbrtFunction.Name);
    }

    [Theory]
    [InlineData(8.0, 2.0)]
    [InlineData(-27.0, -3.0)]
    [InlineData(0.0, 0.0)]
    public void Cbrt_ProducesRealCubeRoot(double input, double expected)
    {
        ValueRef result = Invoke<CbrtFunction>(ValueRef.FromFloat64(input));
        Assert.Equal(expected, result.AsFloat64(), 10);
    }

    // ----- square -----

    [Fact]
    public void Square_Metadata()
    {
        Assert.Equal("square", SquareFunction.Name);
    }

    [Fact]
    public void Square_Int32_ReturnsSquare()
    {
        ValueRef result = Invoke<SquareFunction>(ValueRef.FromInt32(7));
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(49, result.AsInt32());
    }

    [Fact]
    public void Square_Float64_ReturnsSquare()
    {
        ValueRef result = Invoke<SquareFunction>(ValueRef.FromFloat64(2.5));
        Assert.Equal(6.25, result.AsFloat64(), 10);
    }

    [Fact]
    public void Square_NegativeInt_ReturnsPositive()
    {
        ValueRef result = Invoke<SquareFunction>(ValueRef.FromInt32(-4));
        Assert.Equal(16, result.AsInt32());
    }

    // ----- exp / exp2 -----

    [Fact]
    public void Exp_OfZero_IsOne()
    {
        ValueRef result = Invoke<ExpFunction>(ValueRef.FromFloat64(0));
        Assert.Equal(1.0, result.AsFloat64(), 10);
    }

    [Fact]
    public void Exp_OfOne_IsE()
    {
        ValueRef result = Invoke<ExpFunction>(ValueRef.FromFloat64(1.0));
        Assert.Equal(System.Math.E, result.AsFloat64(), 10);
    }

    [Fact]
    public void Exp2_OfTen_IsThousandTwentyFour()
    {
        ValueRef result = Invoke<Exp2Function>(ValueRef.FromFloat64(10.0));
        Assert.Equal(1024.0, result.AsFloat64(), 10);
    }

    // ----- ln / log2 / log10 -----

    [Fact]
    public void Ln_OfE_IsOne()
    {
        ValueRef result = Invoke<LnFunction>(ValueRef.FromFloat64(System.Math.E));
        Assert.Equal(1.0, result.AsFloat64(), 10);
    }

    [Fact]
    public void Log2_Of1024_IsTen()
    {
        ValueRef result = Invoke<Log2Function>(ValueRef.FromFloat64(1024.0));
        Assert.Equal(10.0, result.AsFloat64(), 10);
    }

    [Fact]
    public void Log10_Of1000_IsThree()
    {
        ValueRef result = Invoke<Log10Function>(ValueRef.FromFloat64(1000.0));
        Assert.Equal(3.0, result.AsFloat64(), 10);
    }

    [Fact]
    public void Ln_OfNegative_IsNaN()
    {
        ValueRef result = Invoke<LnFunction>(ValueRef.FromFloat64(-1.0));
        Assert.True(double.IsNaN(result.AsFloat64()));
    }

    // ----- pow / log -----

    [Fact]
    public void Pow_ComputesBasePowerExponent()
    {
        ValueRef result = Invoke<PowFunction>(
            ValueRef.FromFloat64(2.0),
            ValueRef.FromFloat64(8.0));
        Assert.Equal(256.0, result.AsFloat64(), 10);
    }

    [Fact]
    public void Pow_NullArgPropagates()
    {
        ValueRef result = Invoke<PowFunction>(
            ValueRef.Null(DataKind.Float64),
            ValueRef.FromFloat64(8.0));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Log_CustomBase_ComputesLogarithm()
    {
        ValueRef result = Invoke<LogFunction>(
            ValueRef.FromFloat64(8.0),
            ValueRef.FromFloat64(2.0));
        Assert.Equal(3.0, result.AsFloat64(), 10);
    }

    // ----- truncate -----

    [Fact]
    public void Truncate_Metadata()
    {
        Assert.Equal("truncate", TruncateFunction.Name);
    }

    [Theory]
    [InlineData(3.7, 3.0)]
    [InlineData(-3.7, -3.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(-0.5, 0.0)]
    public void Truncate_Float64_RoundsTowardZero(double input, double expected)
    {
        ValueRef result = Invoke<TruncateFunction>(ValueRef.FromFloat64(input));
        Assert.Equal(expected, result.AsFloat64());
    }

    [Fact]
    public void Truncate_Int32_Unchanged()
    {
        ValueRef result = Invoke<TruncateFunction>(ValueRef.FromInt32(42));
        Assert.Equal(42, result.AsInt32());
    }

    // ----- clamp / clip -----

    [Fact]
    public void Clamp_Metadata()
    {
        Assert.Equal("clamp", ClampFunction.Name);
    }

    [Theory]
    [InlineData(5f, 0f, 10f, 5f)]
    [InlineData(-3f, 0f, 10f, 0f)]
    [InlineData(99f, 0f, 10f, 10f)]
    public void Clamp_Scalar_ClampsIntoRange(float v, float lo, float hi, float expected)
    {
        ValueRef result = Invoke<ClampFunction>(
            ValueRef.FromFloat32(v),
            ValueRef.FromFloat32(lo),
            ValueRef.FromFloat32(hi));
        Assert.Equal(expected, result.AsFloat32());
    }

    [Fact]
    public void Clamp_MinGreaterThanMax_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<ClampFunction>(
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(5f)));
    }

    [Fact]
    public void Clamp_NaNBound_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<ClampFunction>(
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(float.NaN),
            ValueRef.FromFloat32(5f)));
    }

    // ----- quantize -----

    [Theory]
    [InlineData(0.37f, 0.1f, 0.4f)]
    [InlineData(0.34f, 0.1f, 0.3f)]
    [InlineData(-0.05f, 0.1f, -0.1f)]
    public void Quantize_RoundsToStepMultiple(float v, float step, float expected)
    {
        ValueRef result = Invoke<QuantizeFunction>(
            ValueRef.FromFloat32(v),
            ValueRef.FromFloat32(step));
        Assert.Equal(expected, result.AsFloat32(), 5);
    }

    [Fact]
    public void Quantize_NonPositiveStep_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<QuantizeFunction>(
            ValueRef.FromFloat32(1f),
            ValueRef.FromFloat32(0f)));
    }

    // ----- bucketize -----

    [Fact]
    public void Bucketize_BeforeFirstBoundary_IsZero()
    {
        ValueRef result = Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(-1f),
            ValueRef.FromPrimitiveArray(new[] { 0f, 1f, 2f }, DataKind.Float32));
        Assert.Equal(0, result.AsInt32());
    }

    [Fact]
    public void Bucketize_BetweenBoundaries()
    {
        // value 0.5 is between 0 and 1 -> bucket 1
        ValueRef result = Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromPrimitiveArray(new[] { 0f, 1f, 2f }, DataKind.Float32));
        Assert.Equal(1, result.AsInt32());
    }

    [Fact]
    public void Bucketize_AboveLastBoundary()
    {
        ValueRef result = Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(10f),
            ValueRef.FromPrimitiveArray(new[] { 0f, 1f, 2f }, DataKind.Float32));
        Assert.Equal(3, result.AsInt32());
    }

    [Fact]
    public void Bucketize_ExactBoundary_GoesRight()
    {
        // value equals boundary[1]=1 -> half-open right -> bucket 2
        ValueRef result = Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(1f),
            ValueRef.FromPrimitiveArray(new[] { 0f, 1f, 2f }, DataKind.Float32));
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public void Bucketize_NonAscendingBoundaries_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(0f),
            ValueRef.FromPrimitiveArray(new[] { 0f, 1f, 1f, 2f }, DataKind.Float32)));
    }

    [Fact]
    public void Bucketize_EmptyBoundaries_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<BucketizeFunction>(
            ValueRef.FromFloat32(0f),
            ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32)));
    }

    // ----- log_softmax -----

    [Fact]
    public void LogSoftmax_Scalar_IsZero()
    {
        ValueRef result = Invoke<LogSoftmaxFunction>(ValueRef.FromFloat32(5f));
        Assert.Equal(0f, result.AsFloat32());
    }

    [Fact]
    public void LogSoftmax_Vector_SumsToOneAfterExp()
    {
        // exp(log_softmax(x)) is the regular softmax; should sum to 1.
        ValueRef result = Invoke<LogSoftmaxFunction>(
            ValueRef.FromPrimitiveArray(new[] { 1f, 2f, 3f }, DataKind.Float32));
        float[] elements = (float[])result.Materialized!;
        double sumExp = 0.0;
        foreach (float e in elements) sumExp += System.Math.Exp(e);
        Assert.Equal(1.0, sumExp, 5);
    }

    [Fact]
    public void LogSoftmax_Stable_OnLargeLogits()
    {
        // Without log-sum-exp trick, exp(1000) overflows. Stable form must
        // still return finite values.
        ValueRef result = Invoke<LogSoftmaxFunction>(
            ValueRef.FromPrimitiveArray(new[] { 1000f, 1001f, 1002f }, DataKind.Float32));
        float[] elements = (float[])result.Materialized!;
        foreach (float e in elements)
        {
            Assert.True(float.IsFinite(e));
        }
    }

    // ----- min_max_normalize -----

    [Fact]
    public void MinMaxNormalize_UInt8_DefaultsTo0_255()
    {
        ValueRef result = Invoke<MinMaxNormalizeFunction>(ValueRef.FromUInt8(255));
        Assert.Equal(1f, result.AsFloat32(), 5);

        ValueRef zero = Invoke<MinMaxNormalizeFunction>(ValueRef.FromUInt8(0));
        Assert.Equal(0f, zero.AsFloat32(), 5);

        ValueRef mid = Invoke<MinMaxNormalizeFunction>(ValueRef.FromUInt8(127));
        Assert.Equal(127f / 255f, mid.AsFloat32(), 5);
    }

    [Fact]
    public void MinMaxNormalize_Int16_DefaultsToFullSignedRange()
    {
        // 0 lands at the midpoint of [-32768, 32767].
        ValueRef result = Invoke<MinMaxNormalizeFunction>(ValueRef.FromInt16(0));
        Assert.InRange(result.AsFloat32(), 0.49f, 0.51f);
    }

    [Fact]
    public void MinMaxNormalize_Float32_WithoutBounds_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => new MinMaxNormalizeFunction()
            .ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void MinMaxNormalize_Float32_WithBounds()
    {
        ValueRef result = Invoke<MinMaxNormalizeFunction>(
            ValueRef.FromFloat32(50f),
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(100f));
        Assert.Equal(0.5f, result.AsFloat32(), 5);
    }

    [Fact]
    public void MinMaxNormalize_MinNotLessThanMax_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<MinMaxNormalizeFunction>(
            ValueRef.FromFloat32(5f),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(10f)));
    }

    [Fact]
    public void MinMaxNormalize_UInt8Array_NormalizesElementwise()
    {
        byte[] pixels = { 0, 64, 128, 255 };
        ValueRef arr = ValueRef.FromPrimitiveArray(pixels, DataKind.UInt8);
        ValueRef result = Invoke<MinMaxNormalizeFunction>(arr);
        float[] elements = (float[])result.Materialized!;
        Assert.Equal(0f, elements[0], 5);
        Assert.Equal(64f / 255f, elements[1], 5);
        Assert.Equal(128f / 255f, elements[2], 5);
        Assert.Equal(1f, elements[3], 5);
    }

    [Fact]
    public void MinMaxNormalize_NullPropagates()
    {
        ValueRef result = Invoke<MinMaxNormalizeFunction>(ValueRef.Null(DataKind.UInt8));
        Assert.True(result.IsNull);
    }

    // ----- denormalize -----

    [Fact]
    public void Denormalize_InvertsMinMaxNormalize()
    {
        ValueRef normalized = Invoke<MinMaxNormalizeFunction>(
            ValueRef.FromFloat32(75f),
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(100f));
        ValueRef back = Invoke<DenormalizeFunction>(
            normalized,
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(100f));
        Assert.Equal(75f, back.AsFloat32(), 4);
    }

    [Fact]
    public void Denormalize_NegativeMin_PreservesOffset()
    {
        // [-50, 50]: 0.5 maps back to 0.
        ValueRef result = Invoke<DenormalizeFunction>(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromFloat32(-50f),
            ValueRef.FromFloat32(50f));
        Assert.Equal(0f, result.AsFloat32(), 5);
    }

    [Fact]
    public void Denormalize_InvalidBounds_Throws()
    {
        Assert.Throws<FunctionArgumentException>(() => Invoke<DenormalizeFunction>(
            ValueRef.FromFloat32(0.5f),
            ValueRef.FromFloat32(10f),
            ValueRef.FromFloat32(0f)));
    }

    // ----- registry / alias wiring -----

    [Theory]
    [InlineData("sign")]
    [InlineData("negate")]
    [InlineData("cbrt")]
    [InlineData("square")]
    [InlineData("exp")]
    [InlineData("exp2")]
    [InlineData("ln")]
    [InlineData("log2")]
    [InlineData("log10")]
    [InlineData("pow")]
    [InlineData("log")]
    [InlineData("truncate")]
    [InlineData("trunc")]
    [InlineData("clamp")]
    [InlineData("clip")]
    [InlineData("quantize")]
    [InlineData("bucketize")]
    [InlineData("log_softmax")]
    [InlineData("min_max_normalize")]
    [InlineData("denormalize")]
    public void Function_RegisteredByName(string name)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar(name);
        Assert.NotNull(function);
    }

    // ----- helpers -----

    private static ValueRef Invoke<T>(params ValueRef[] arguments)
        where T : IScalarFunction, new()
    {
        T function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }
}
