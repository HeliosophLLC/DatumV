using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Inference;

/// <summary>
/// Unit tests for <see cref="InferFunction.ReadAllOutputsAsStruct"/> —
/// the reader that turns a multi-output ONNX dispatch into a SQL Struct
/// keyed by output name. Verifies field naming, shape preservation, and
/// mixed element-kind handling without needing a real ONNX session.
/// </summary>
/// <remarks>
/// Multi-dim shape preservation is the diagnostically interesting case:
/// bodies like depth-anything-v3-large rely on the struct field carrying
/// a rank-4 shape through to downstream consumers (<c>depth_map_to_image</c>,
/// <c>array_resize_2d</c>). Any regression that flattens the field at
/// the struct boundary would surface here first.
/// </remarks>
public sealed class InferFunctionReadAllOutputsAsStructTests
{
    private const string ModelName = "stub_model";

    private static IInferenceSession SessionWithOutputs(params TensorSpec[] outputs) =>
        new ReaderStubSession(outputs);

    private static StubTensorBag BagWith(params (string name, DataKind kind, int[] shape, Array data)[] tensors)
    {
        StubTensorBag bag = new();
        foreach (var (name, kind, shape, data) in tensors)
        {
            switch (kind)
            {
                case DataKind.Float32:
                    bag.Add<float>(name, kind, shape, (float[])data);
                    break;
                case DataKind.Int64:
                    bag.Add<long>(name, kind, shape, (long[])data);
                    break;
                case DataKind.Int32:
                    bag.Add<int>(name, kind, shape, (int[])data);
                    break;
                default:
                    throw new NotSupportedException($"Test scaffold doesn't handle {kind} yet.");
            }
        }
        return bag;
    }

    [Fact]
    public void ReadAllOutputsAsStruct_SingleFloat32Rank1_SurfacesAsArrayField()
    {
        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("logits", DataKind.Float32, [3]));
        using StubTensorBag bag = BagWith(("logits", DataKind.Float32, [3], new float[] { 0.1f, 0.2f, 0.7f }));

        ValueRef result = InferFunction.ReadAllOutputsAsStruct(
            session, bag, types: null, modelName: ModelName, arena: null);

        Assert.Equal(DataKind.Struct, result.Kind);
        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(1, fields.Length);
        Assert.True(fields[0].IsArray);
        Assert.Equal(DataKind.Float32, fields[0].ArrayElementKind);
        Assert.False(fields[0].IsMultiDim);
    }

    [Fact]
    public void ReadAllOutputsAsStruct_MultiDimOutput_PreservesShape()
    {
        // The diagnostically interesting case: rank-4 [1, 4, 2, 3] output
        // (mask-decoder-style). The struct field must surface as multi-dim
        // so downstream consumers can read the shape; a regression that
        // flattens it would surface in depth-anything-v3 / SAM bodies.
        int[] shape = [1, 4, 2, 3];
        int total = 1 * 4 * 2 * 3;
        float[] data = new float[total];
        for (int i = 0; i < total; i++) data[i] = i;

        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("masks", DataKind.Float32, [1, 4, 2, 3]));
        using StubTensorBag bag = BagWith(("masks", DataKind.Float32, shape, data));

        ValueRef result = InferFunction.ReadAllOutputsAsStruct(
            session, bag, types: null, modelName: ModelName, arena: null);

        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(1, fields.Length);
        Assert.True(fields[0].IsArray);
        Assert.True(fields[0].IsMultiDim,
            "rank-4 output field lost its multi-dim shape — downstream consumers " +
            "that index with multiple dims (or rely on shape metadata) will misbehave.");
        Assert.Equal(DataKind.Float32, fields[0].ArrayElementKind);
    }

    [Fact]
    public void ReadAllOutputsAsStruct_MultipleOutputs_PreservesOrderAndKinds()
    {
        // SAM-decoder-style dispatch: [masks (Float32, rank-4),
        // iou_predictions (Float32, rank-2)] both come back; order
        // matches the session's Outputs declaration; both fields are
        // accessible via name and ordinal.
        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("masks", DataKind.Float32, [1, 4, 2, 2]),
            new TensorSpec("iou_predictions", DataKind.Float32, [1, 4]));
        using StubTensorBag bag = BagWith(
            ("masks", DataKind.Float32, [1, 4, 2, 2], new float[16]),
            ("iou_predictions", DataKind.Float32, [1, 4], new float[] { 0.9f, 0.7f, 0.3f, 0.1f }));

        ValueRef result = InferFunction.ReadAllOutputsAsStruct(
            session, bag, types: null, modelName: ModelName, arena: null);

        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(2, fields.Length);
        // Order matches the session Outputs declaration.
        Assert.True(fields[0].IsMultiDim, "masks field lost its multi-dim shape.");
        Assert.True(fields[1].IsMultiDim, "iou_predictions field lost its multi-dim shape.");
        Assert.Equal(DataKind.Float32, fields[0].ArrayElementKind);
        Assert.Equal(DataKind.Float32, fields[1].ArrayElementKind);
    }

    [Fact]
    public void ReadAllOutputsAsStruct_MixedKindOutputs_KeepsEachFieldKind()
    {
        // RT-DETR-style two-output session: Float32 logits + Int64 box ids.
        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("logits", DataKind.Float32, [1, 3]),
            new TensorSpec("indices", DataKind.Int64, [1, 3]));
        using StubTensorBag bag = BagWith(
            ("logits", DataKind.Float32, [1, 3], new float[] { 0.1f, 0.2f, 0.7f }),
            ("indices", DataKind.Int64, [1, 3], new long[] { 4, 7, 2 }));

        ValueRef result = InferFunction.ReadAllOutputsAsStruct(
            session, bag, types: null, modelName: ModelName, arena: null);

        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(2, fields.Length);
        Assert.Equal(DataKind.Float32, fields[0].ArrayElementKind);
        Assert.Equal(DataKind.Int64, fields[1].ArrayElementKind);
    }

    [Fact]
    public void ReadAllOutputsAsStruct_Rank0ScalarOutput_SurfacesAsScalarField()
    {
        // Rank-0 = scalar. Should surface as a scalar value (IsArray=false)
        // so SQL-side `result['loss']` returns Float32 not Float32[].
        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("loss", DataKind.Float32, []));
        using StubTensorBag bag = BagWith(("loss", DataKind.Float32, [], new float[] { 0.42f }));

        ValueRef result = InferFunction.ReadAllOutputsAsStruct(
            session, bag, types: null, modelName: ModelName, arena: null);

        ReadOnlySpan<ValueRef> fields = result.GetStructFields();
        Assert.Equal(1, fields.Length);
        Assert.False(fields[0].IsArray,
            "rank-0 scalar output surfaced as an array — downstream `result['loss']` " +
            "indexing would yield the wrong shape.");
        Assert.Equal(DataKind.Float32, fields[0].Kind);
        Assert.Equal(0.42f, fields[0].AsFloat32(), 5);
    }

    [Fact]
    public void ReadAllOutputsAsStruct_MissingTensor_ThrowsClearError()
    {
        // The session declares an output but the bag came back empty —
        // surface that as a clear "session implementation misconfigured"
        // error rather than a NullReferenceException somewhere downstream.
        IInferenceSession session = SessionWithOutputs(
            new TensorSpec("absent", DataKind.Float32, [1]));
        using StubTensorBag empty = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InferFunction.ReadAllOutputsAsStruct(
                session, empty, types: null, modelName: ModelName, arena: null));
        Assert.Contains("absent", ex.Message);
        Assert.Contains(ModelName, ex.Message);
    }

    /// <summary>
    /// Stub session that surfaces a fixed output spec list. Inputs and
    /// dispatch aren't exercised — <see cref="ReadAllOutputsAsStruct"/>
    /// only reads <see cref="IInferenceSession.Outputs"/>.
    /// </summary>
    private sealed class ReaderStubSession : IInferenceSession
    {
        public ReaderStubSession(IReadOnlyList<TensorSpec> outputs)
        {
            Outputs = outputs;
        }
        public IReadOnlyList<TensorSpec> Inputs => Array.Empty<TensorSpec>();
        public IReadOnlyList<TensorSpec> Outputs { get; }
        public InferenceBackendId Backend => InferenceBackendId.OnnxRuntime;
        public InferenceDevice Device => InferenceDevice.OnnxRuntimeCpu;
        public long EstimatedResidentBytes => 0;
        public TensorBag CreateInputBag() => new StubTensorBag();
        public ValueTask<TensorBag> RunAsync(TensorBag _, CancellationToken __) =>
            throw new NotSupportedException("Output-reader test stub; Run not used.");
        public void Dispose() { }
    }
}
