using DatumIngest.Functions;
using DatumIngest.Inference;
using DatumIngest.Model;

namespace DatumIngest.Tests.Inference;

/// <summary>
/// Unit tests for <see cref="InferFunction.AddInputTensor"/> — the
/// element-kind dispatcher that marshals a SQL ValueRef into a TensorBag
/// slot. Covers the Int32↔Int64 cross-kind coercion (a recent addition
/// motivated by the CLIP-style "tokenizer returns Int64 but session
/// declares Int32 input" case) including the overflow-detection branch
/// in the narrowing path.
/// </summary>
public sealed class InferFunctionAddInputTensorTests
{
    private static TensorSpec Spec(string name, DataKind kind, params int?[] shape) =>
        new(name, kind, shape);

    [Fact]
    public void AddInputTensor_Int32SessionInput_AcceptsInt32Array()
    {
        StubTensorBag bag = new();
        int[] values = [1, 2, 3];
        ValueRef arg = ValueRef.FromPrimitiveArray(values, DataKind.Int32);

        InferFunction.AddInputTensor(
            bag,
            Spec("input", DataKind.Int32, 3),
            arg,
            modelName: "stub",
            explicitShape: null);

        Assert.True(bag.TryGet("input", out IInferenceTensor tensor));
        Assert.Equal(DataKind.Int32, tensor.ElementKind);
        ReadOnlySpan<int> packed = tensor.AsSpan<int>();
        Assert.Equal(values, packed.ToArray());
    }

    [Fact]
    public void AddInputTensor_Int32SessionInput_NarrowsInt64Array()
    {
        // Whisper-class tokenizers return Int64[] by convention but several
        // session input specs declare Int32 (CLIP text encoder, SAM
        // coordinate prompts). The dispatcher should narrow per-element
        // when values fit, not throw.
        StubTensorBag bag = new();
        long[] tokens = [49406L, 320L, 5000L, 49407L];
        ValueRef arg = ValueRef.FromPrimitiveArray(tokens, DataKind.Int64);

        InferFunction.AddInputTensor(
            bag,
            Spec("input_ids", DataKind.Int32, 4),
            arg,
            modelName: "stub",
            explicitShape: null);

        Assert.True(bag.TryGet("input_ids", out IInferenceTensor tensor));
        Assert.Equal(DataKind.Int32, tensor.ElementKind);
        ReadOnlySpan<int> packed = tensor.AsSpan<int>();
        Assert.Equal(new[] { 49406, 320, 5000, 49407 }, packed.ToArray());
    }

    [Fact]
    public void AddInputTensor_Int32SessionInput_Int64OverflowSurfacesIndexAndValue()
    {
        // Element [2] is outside Int32 range — the narrowing path must
        // throw with the index and value in the message so the failure
        // points at the SQL body (e.g. a bad CAST upstream) rather than
        // a generic "input shape" error deep inside ORT.
        StubTensorBag bag = new();
        long[] tokens = [42L, 100L, (long)int.MaxValue + 7L, 0L];
        ValueRef arg = ValueRef.FromPrimitiveArray(tokens, DataKind.Int64);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InferFunction.AddInputTensor(
                bag,
                Spec("input_ids", DataKind.Int32, 4),
                arg,
                modelName: "stub",
                explicitShape: null));

        Assert.Contains("[2]", ex.Message);
        Assert.Contains(tokens[2].ToString(), ex.Message);
        // No partial fill should be observable to the session.
        Assert.Equal(0, bag.Count);
    }

    [Fact]
    public void AddInputTensor_Int64SessionInput_WidensInt32Array()
    {
        // The reverse direction: session declares Int64, body built the
        // array with Int32 element kind (array_repeat default, etc.).
        // Widening is always safe; the marshaler should do it transparently.
        StubTensorBag bag = new();
        int[] values = [1, 2, int.MaxValue];
        ValueRef arg = ValueRef.FromPrimitiveArray(values, DataKind.Int32);

        InferFunction.AddInputTensor(
            bag,
            Spec("input_ids", DataKind.Int64, 3),
            arg,
            modelName: "stub",
            explicitShape: null);

        Assert.True(bag.TryGet("input_ids", out IInferenceTensor tensor));
        Assert.Equal(DataKind.Int64, tensor.ElementKind);
        ReadOnlySpan<long> packed = tensor.AsSpan<long>();
        Assert.Equal(new[] { 1L, 2L, (long)int.MaxValue }, packed.ToArray());
    }

    [Fact]
    public void AddInputTensor_Float32SessionInput_RoundTripsFloat32Array()
    {
        StubTensorBag bag = new();
        float[] values = [0.1f, -0.2f, 0.3f, 0.0f];
        ValueRef arg = ValueRef.FromPrimitiveArray(values, DataKind.Float32);

        InferFunction.AddInputTensor(
            bag,
            Spec("input", DataKind.Float32, 4),
            arg,
            modelName: "stub",
            explicitShape: null);

        Assert.True(bag.TryGet("input", out IInferenceTensor tensor));
        Assert.Equal(DataKind.Float32, tensor.ElementKind);
        ReadOnlySpan<float> packed = tensor.AsSpan<float>();
        Assert.Equal(values, packed.ToArray());
    }

    [Fact]
    public void AddInputTensor_Float16SessionInput_NarrowsFloat32Array()
    {
        // SDXL fp16 + Florence-2 fp16 take Float32 from the SQL body and
        // need an fp16 cast at the input boundary. v1 supports Float32 in
        // and Half out; the per-element cast loop is small but
        // numerically sensitive.
        StubTensorBag bag = new();
        float[] values = [0.5f, -1.5f, 65000f, -65000f];
        ValueRef arg = ValueRef.FromPrimitiveArray(values, DataKind.Float32);

        InferFunction.AddInputTensor(
            bag,
            Spec("input", DataKind.Float16, 4),
            arg,
            modelName: "stub",
            explicitShape: null);

        Assert.True(bag.TryGet("input", out IInferenceTensor tensor));
        Assert.Equal(DataKind.Float16, tensor.ElementKind);
        ReadOnlySpan<Half> packed = tensor.AsSpan<Half>();
        Assert.Equal(values.Length, packed.Length);
        // Element-wise cast equivalence.
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal((Half)values[i], packed[i]);
        }
    }

    [Fact]
    public void AddInputTensor_NullArgumentThrows()
    {
        StubTensorBag bag = new();
        ValueRef nullArg = ValueRef.NullArray(DataKind.Int64);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InferFunction.AddInputTensor(
                bag,
                Spec("input_ids", DataKind.Int64, 3),
                nullArg,
                modelName: "stub",
                explicitShape: null));
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddInputTensor_ExplicitShape_OverridesSpec()
    {
        // The 2-arg infer() form passes an explicit shape that bypasses
        // ResolveShape — useful when the session spec leaves multiple
        // dims dynamic. The marshaler must use the supplied shape verbatim
        // instead of probing the spec.
        StubTensorBag bag = new();
        float[] values = new float[6];
        for (int i = 0; i < 6; i++) values[i] = i;
        ValueRef arg = ValueRef.FromPrimitiveArray(values, DataKind.Float32);

        int[] explicitShape = [1, 2, 3];
        InferFunction.AddInputTensor(
            bag,
            Spec("input", DataKind.Float32, null, null, null),
            arg,
            modelName: "stub",
            explicitShape: explicitShape);

        Assert.True(bag.TryGet("input", out IInferenceTensor tensor));
        Assert.Equal(explicitShape, tensor.Shape.ToArray());
    }
}
