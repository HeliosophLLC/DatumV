using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Helpers for reading ONNX session outputs into <see cref="DenseTensor{T}"/>
/// without caring whether the underlying graph is fp32 or fp16. ORT
/// auto-casts <i>inputs</i> at session-boundary (fp32 caller → fp16 graph
/// works transparently), but <i>outputs</i> retain the graph's native
/// type — fp16 graphs return <see cref="Float16"/> tensors that throw
/// NullReferenceException when read via <c>AsTensor&lt;float&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Diffusion models exported with <c>optimum-cli --dtype fp16</c> halve
/// disk + VRAM footprint vs fp32 at no measurable quality cost, but
/// require this boundary cast to integrate with float32 pipeline code.
/// The Float16 → float32 cast is O(N) over the flat output buffer and
/// adds &lt;1% to total inference time for typical text-encoder / VAE
/// outputs (millions of values, microseconds per million).
/// </para>
/// <para>
/// <strong>Why not change the pipeline to use Half throughout.</strong>
/// .NET's <see cref="Half"/> arithmetic is software-emulated on most
/// CPUs (no hardware fp16 ALU), so any per-element math we did on
/// intermediate tensors would actually run slower than fp32. The cast
/// at boundaries is the right place to pay the conversion cost.
/// </para>
/// </remarks>
internal static class OnnxTensorConversion
{
    /// <summary>
    /// Wraps a <c>DenseTensor&lt;float&gt;</c> input as a
    /// <see cref="NamedOnnxValue"/> for <paramref name="session"/>, casting
    /// to <see cref="Float16"/> first when the session's named input
    /// expects fp16. ORT does not auto-cast inputs across precision —
    /// passing a Float tensor to an expected-Float16 port raises
    /// <c>OnnxRuntimeException: Tensor element data type discovered:
    /// Float metadata expected: Float16</c>.
    /// </summary>
    /// <remarks>
    /// Use this for every float-typed model input on a session that
    /// might be fp16. Int64 token-id inputs need
    /// <see cref="CreateAutoCastTokenInput"/>: some optimized exports
    /// (e.g. Microsoft's onnxruntime/sdxl-turbo) declare token-id
    /// metadata as <c>int32</c> rather than <c>int64</c>.
    /// </remarks>
    public static NamedOnnxValue CreateAutoCastInput(
        InferenceSession session, string inputName, DenseTensor<float> tensor)
    {
        NodeMetadata meta = session.InputMetadata[inputName];
        if (meta.ElementType == typeof(Float16))
        {
            DenseTensor<Float16> halfTensor = ToFloat16Tensor(tensor);
            return NamedOnnxValue.CreateFromTensor(inputName, halfTensor);
        }
        return NamedOnnxValue.CreateFromTensor(inputName, tensor);
    }

    /// <summary>
    /// Wraps an int64 token-id array as a <see cref="NamedOnnxValue"/>,
    /// downcasting to int32 first when the session's named input
    /// expects <c>tensor(int32)</c>. Some optimized text-encoder exports
    /// (notably Microsoft's CUDA-targeted SDXL-Turbo) declare token IDs
    /// as int32 to match CUDA kernel preferences; passing an int64
    /// tensor raises <c>InvalidArgument: Tensor element data type
    /// discovered: Int64 metadata expected: Int32</c>.
    /// </summary>
    public static NamedOnnxValue CreateAutoCastTokenInput(
        InferenceSession session, string inputName, long[] tokenIds, ReadOnlySpan<int> shape)
    {
        NodeMetadata meta = session.InputMetadata[inputName];
        if (meta.ElementType == typeof(int))
        {
            int[] tokenIdsInt32 = new int[tokenIds.Length];
            for (int i = 0; i < tokenIds.Length; i++)
            {
                tokenIdsInt32[i] = (int)tokenIds[i];
            }
            return NamedOnnxValue.CreateFromTensor(
                inputName, new DenseTensor<int>(tokenIdsInt32, shape));
        }
        return NamedOnnxValue.CreateFromTensor(
            inputName, new DenseTensor<long>(tokenIds, shape));
    }

    /// <summary>
    /// Element-wise cast of a <c>DenseTensor&lt;float&gt;</c> to
    /// <c>DenseTensor&lt;Float16&gt;</c>. Allocates a new buffer.
    /// </summary>
    public static DenseTensor<Float16> ToFloat16Tensor(DenseTensor<float> input)
    {
        ReadOnlySpan<float> floatSpan = input.Buffer.Span;
        Float16[] result = new Float16[floatSpan.Length];
        for (int i = 0; i < floatSpan.Length; i++)
        {
            result[i] = (Float16)floatSpan[i];
        }
        return new DenseTensor<Float16>(result, input.Dimensions);
    }

    /// <summary>
    /// Reads an ONNX session output as a <c>DenseTensor&lt;float&gt;</c>,
    /// handling both fp32 and fp16 graphs. For fp32 graphs this is a
    /// thin wrapper over <c>AsTensor&lt;float&gt;().ToDenseTensor()</c>;
    /// for fp16 graphs it casts each Float16 element to fp32 in a tight
    /// loop.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The output is neither <see cref="float"/> nor <see cref="Float16"/>
    /// (e.g. integer-typed outputs from a classifier — those need their
    /// own typed reader).
    /// </exception>
    public static DenseTensor<float> ToFloatTensor(DisposableNamedOnnxValue output)
    {
        // Common path: fp32 graph. AsTensor<T> returns null on type
        // mismatch (not throw) — that's how we detect fp16.
        Tensor<float>? floatTensor = output.AsTensor<float>();
        if (floatTensor is not null)
        {
            return floatTensor.ToDenseTensor();
        }

        // fp16 graph: cast every element to fp32. Float16.ToFloat() is
        // a hardware-friendly bit reinterpretation; it doesn't allocate.
        Tensor<Float16>? halfTensor = output.AsTensor<Float16>();
        if (halfTensor is not null)
        {
            DenseTensor<Float16> dense = halfTensor.ToDenseTensor();
            ReadOnlySpan<Float16> halfSpan = dense.Buffer.Span;
            float[] result = new float[halfSpan.Length];
            for (int i = 0; i < halfSpan.Length; i++)
            {
                result[i] = halfSpan[i].ToFloat();
            }
            return new DenseTensor<float>(result, dense.Dimensions);
        }

        throw new InvalidOperationException(
            $"ONNX output '{output.Name}' is neither tensor(float) nor tensor(float16). " +
            $"For non-float outputs (int64 detection labels, etc.), use a typed reader directly.");
    }
}
