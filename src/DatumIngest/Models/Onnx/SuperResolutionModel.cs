using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Generic super-resolution wrapper for Real-ESRGAN-Compact (SRVGGNet)
/// style ONNX exports — input <c>Image</c>, output <c>Image</c> upscaled
/// by a fixed integer factor (4× for the realesr-general-x4v3 export).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input pipeline.</strong> Each image is decoded via SkiaSharp,
/// kept at its native resolution, and packed as <strong>NCHW</strong>
/// float32 in <strong>RGB</strong> channel order with simple
/// <c>pixel / 255</c> normalisation — Real-ESRGAN-Compact trains on the
/// raw <c>[0, 1]</c> range with no per-channel mean/std. The graph's
/// spatial dims are dynamic, so we resize the tensor to match the input
/// rather than letterboxing to a fixed size.
/// </para>
/// <para>
/// <strong>Output pipeline.</strong> The graph emits a single
/// <c>[1, 3, H*scale, W*scale]</c> tensor of float32 in <c>[0, 1]</c>.
/// We clip, scale to <c>[0, 255]</c>, pack into an RGBA <see cref="SKBitmap"/>
/// (alpha forced opaque), and hand the bitmap straight to
/// <see cref="ValueRef.FromImage(SKBitmap)"/> — no PNG round-trip. Downstream
/// consumers that need bytes still encode lazily; consumers that need pixels
/// (the dev-web image renderer, follow-up vision models) get them without
/// re-decoding.
/// </para>
/// <para>
/// <strong>Per-row dispatch.</strong> Image rows can have different
/// resolutions, so each row runs its own <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// call. The base <see cref="OnnxModel"/> template-method pattern assumes
/// uniform shapes across the batch; we override <see cref="InferBatchAsync"/>
/// directly.
/// </para>
/// <para>
/// <strong>Memory.</strong> Whole-image inference: the float NCHW
/// intermediates cost <c>3 × H × W × 4</c> bytes for input plus
/// <c>3 × (H*scale) × (W*scale) × 4</c> bytes for output. For a
/// 1024×1024 input at 4×, that's ~210 MB of intermediate floats. Tile-based
/// inference (with overlap) is the right follow-up for high-resolution
/// inputs but not implemented here.
/// </para>
/// </remarks>
public sealed class SuperResolutionModel : OnnxModel
{
    private const int InputChannels = 3;

    private readonly string _onnxInputName;
    private readonly int _scaleFactor;
    private readonly float _defaultOutscale;

    /// <summary>
    /// Construction-time default output-scale factor. Per-row callers can
    /// override via the optional <c>outscale</c> argument. Must lie in
    /// <c>[1.0, scaleFactor]</c>; values above the native scale are
    /// rejected because the model can't produce more pixels than its
    /// architecture supports.
    /// </summary>
    public float DefaultOutscale => _defaultOutscale;

    /// <summary>
    /// Loads a Real-ESRGAN-Compact-style ONNX from <paramref name="modelFilePath"/>.
    /// Validates that the graph has a single image input with dynamic
    /// spatial dims and a single image output, and records the native
    /// upscale factor declared by <paramref name="scaleFactor"/> (4 for
    /// the realesr-general-x4v3 export).
    /// </summary>
    /// <param name="name">Catalog-visible model name.</param>
    /// <param name="modelFilePath">Absolute path to the ONNX file.</param>
    /// <param name="scaleFactor">
    /// The model's native upscale factor. 4 for realesr-general-x4v3.
    /// </param>
    /// <param name="defaultOutscale">
    /// Default per-call output scale. <c>null</c> means "use the native
    /// scale factor" (i.e. don't resize). Must be in
    /// <c>[1.0, scaleFactor]</c>.
    /// </param>
    public SuperResolutionModel(
        string name,
        string modelFilePath,
        int scaleFactor = 4,
        float? defaultOutscale = null)
        : base(
            name,
            modelFilePath,
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Image,
            isDeterministic: true)
    {
        if (scaleFactor < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleFactor),
                scaleFactor,
                "Super-resolution scale factor must be at least 1.");
        }

        _scaleFactor = scaleFactor;
        _defaultOutscale = defaultOutscale ?? scaleFactor;
        if (_defaultOutscale < 1f || _defaultOutscale > scaleFactor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultOutscale),
                _defaultOutscale,
                $"defaultOutscale must be in [1.0, {scaleFactor}]; got {_defaultOutscale}.");
        }

        if (Session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"SuperResolutionModel expected exactly one ONNX input but '{modelFilePath}' has "
                + $"{Session.InputMetadata.Count}: [{string.Join(", ", Session.InputMetadata.Keys)}]. "
                + "The realesr-general-x4v3 single-input export is supported; the dual-weight "
                + "(dni_weight) variant is not.");
        }

        _onnxInputName = Session.InputMetadata.Keys.First();

        // Real-ESRGAN-Compact exports declare [N, 3, H, W] with dynamic
        // H and W (-1 in OnnxRuntime). A fixed-spatial-dim export would
        // silently break on real inputs, so reject it up-front.
        int[] inputDims = Session.InputMetadata[_onnxInputName].Dimensions;
        if (inputDims.Length != 4 || inputDims[1] != InputChannels)
        {
            throw new InvalidOperationException(
                $"SuperResolutionModel expected input shape [N, {InputChannels}, H, W] but '{modelFilePath}' "
                + $"declares [{string.Join(", ", inputDims)}]. Was the ONNX exported for a different architecture?");
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        ValueRef[] results = new ValueRef[inputs.Count];

        for (int row = 0; row < inputs.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValueRef image = inputs[row][0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"SuperResolutionModel received a null image at row {row}; filter nulls upstream before invoking the model.");
            }

            // Optional per-row hyperparameter:
            //   [0] outscale (Float64) — output scale relative to input.
            // Defaults to the construction-time DefaultOutscale (typically
            // the native scale factor). Must be in [1.0, scaleFactor]:
            // values above scaleFactor would require upsampling beyond the
            // network's native resolution, which defeats the purpose.
            IReadOnlyList<ValueRef> rowOverrides = overrides.Count > row
                ? overrides[row]
                : [];
            float rowOutscale = rowOverrides.Count > 0 && !rowOverrides[0].IsNull
                ? rowOverrides[0].ToFloat()
                : _defaultOutscale;
            if (rowOutscale < 1f || rowOutscale > _scaleFactor)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overrides),
                    rowOutscale,
                    $"outscale must be in [1.0, {_scaleFactor}] (got {rowOutscale} at row {row}). "
                    + $"The model produces at most {_scaleFactor}× pixels — values above that would upsample beyond the network's native resolution.");
            }

            SKBitmap decoded = image.AsImage();
            float capturedOutscale = rowOutscale;
            results[row] = await Task.Run<ValueRef>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return UpscaleSingleImage(decoded, capturedOutscale);
            }, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "SuperResolutionModel overrides InferBatchAsync directly because input rows have variable spatial dims. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "SuperResolutionModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private ValueRef UpscaleSingleImage(SKBitmap decoded, float outscale)
    {
        int width = decoded.Width;
        int height = decoded.Height;
        int planeSize = width * height;

        // The arena-owned bitmap from `ValueRef.AsImage()` is in the
        // platform-native colour type — BGRA8888 on Windows, RGBA8888 on
        // most other platforms. Other models route through
        // `decoded.Resize(targetInfo, …)` with Rgba8888 in the target info,
        // which doubles as a colour-order conversion; we don't resize, so
        // force an explicit copy into RGBA8888 here. Skipping this swap
        // leaves red and blue inverted at the input, which the network
        // "fixes" by routing the R signal through its B-trained weights —
        // the visible symptom is reddish/greenish content shifting blue
        // (and vice versa) in the upscaled output.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap rgba = new(rgbaInfo);
        if (!decoded.CopyTo(rgba, SKColorType.Rgba8888))
        {
            throw new InvalidOperationException(
                $"SkiaSharp failed to convert the input image to RGBA8888 (source colour type: {decoded.ColorType}).");
        }

        // Pack the source bitmap as NCHW float32 RGB in [0, 1]. Real-ESRGAN
        // -Compact has no per-channel mean/std — raw normalisation is correct.
        float[] inputData = new float[InputChannels * planeSize];
        nint pixelPtr = rgba.GetPixels();
        unsafe
        {
            byte* source = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int srcOffset = yx * 4;
                inputData[yx] = source[srcOffset + 0] / 255f;                   // R plane
                inputData[planeSize + yx] = source[srcOffset + 1] / 255f;       // G plane
                inputData[2 * planeSize + yx] = source[srcOffset + 2] / 255f;   // B plane
            }
        }

        DenseTensor<float> inputTensor = new(
            inputData,
            [1, InputChannels, height, width]);
        NamedOnnxValue input = OnnxTensorConversion.CreateAutoCastInput(Session, _onnxInputName, inputTensor);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([input]);

        DisposableNamedOnnxValue first = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("Super-resolution ONNX session returned no outputs.");

        DenseTensor<float> outTensor = OnnxTensorConversion.ToFloatTensor(first);
        int[] outDims = outTensor.Dimensions.ToArray();

        int expectedW = width * _scaleFactor;
        int expectedH = height * _scaleFactor;
        if (outDims.Length != 4 || outDims[0] != 1 || outDims[1] != InputChannels
            || outDims[2] != expectedH || outDims[3] != expectedW)
        {
            throw new InvalidOperationException(
                $"Super-resolution output shape mismatch: expected [1, {InputChannels}, {expectedH}, {expectedW}] "
                + $"for {width}×{height} input at {_scaleFactor}× scale, got [{string.Join(", ", outDims)}].");
        }

        ReadOnlySpan<float> flat = outTensor.Buffer.Span;
        int outPlaneSize = expectedW * expectedH;

        SKImageInfo info = new(expectedW, expectedH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap nativeScale = new(info);
        nint outPixelPtr = nativeScale.GetPixels();
        unsafe
        {
            byte* dest = (byte*)outPixelPtr;
            for (int yx = 0; yx < outPlaneSize; yx++)
            {
                float r = flat[yx];
                float g = flat[outPlaneSize + yx];
                float b = flat[2 * outPlaneSize + yx];

                dest[yx * 4 + 0] = NormalizeToByte(r);
                dest[yx * 4 + 1] = NormalizeToByte(g);
                dest[yx * 4 + 2] = NormalizeToByte(b);
                dest[yx * 4 + 3] = 255;
            }
        }

        // Common case: caller wants the native scale — hand the bitmap
        // off without an intermediate resize. Ownership transfers to the
        // ValueRef, so no `using` here.
        if (MathF.Abs(outscale - _scaleFactor) < 1e-4f)
        {
            return ValueRef.FromImage(nativeScale);
        }

        // outscale < native scale: downsample via SkiaSharp (bicubic-ish).
        // outscale is pre-validated to be in [1.0, scaleFactor] by the caller.
        int targetW = Math.Max(1, (int)MathF.Round(width * outscale));
        int targetH = Math.Max(1, (int)MathF.Round(height * outscale));
        SKImageInfo targetInfo = new(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (nativeScale)
        {
            SKBitmap resized = nativeScale.Resize(targetInfo, SKSamplingOptions.Default)
                ?? throw new InvalidOperationException(
                    $"SkiaSharp failed to resize the {_scaleFactor}× output to outscale={outscale} ({targetW}×{targetH}).");
            return ValueRef.FromImage(resized);
        }
    }

    /// <summary>
    /// Maps a <c>[0, 1]</c> float to a <c>[0, 255]</c> byte with clamp.
    /// Real-ESRGAN-Compact's output range is [0, 1]; out-of-range values
    /// are saturation artefacts and clamping is the standard remedy.
    /// </summary>
    private static byte NormalizeToByte(float value)
    {
        float scaled = value * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }
}
