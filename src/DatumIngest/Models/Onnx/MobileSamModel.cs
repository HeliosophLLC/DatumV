using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// MobileSAM prompted segmentation — input <c>(image, x, y)</c>, output
/// <c>Image</c> single-channel binary mask sized to match the input image.
/// Wraps the <c>vietanhdev/samexporter</c> two-file ONNX export
/// (<c>mobile_sam_image_encoder.onnx</c> + <c>sam_mask_decoder_multi.onnx</c>
/// or <c>sam_mask_decoder_single.onnx</c>) under one model class.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture.</strong> Two ONNX sessions form a one-shot pipeline:
/// the TinyViT image encoder (~28 MB) maps a per-row bitmap to a
/// <c>[1, 256, 64, 64]</c> embedding; the prompt-conditioned mask decoder
/// (~16 MB) consumes that embedding plus a single foreground point and emits
/// a binary mask at the input image's original resolution. The encoder is
/// the heavy step; the decoder runs in tens of milliseconds.
/// </para>
/// <para>
/// <strong>Why samexporter exports.</strong> The encoder declared in these
/// files takes <c>[H, W, 3]</c> raw uint8-range float32 — preprocessing
/// (resize-longest-side-to-1024, ImageNet normalisation, pad-to-1024×1024) is
/// baked into the ONNX graph. The decoder takes the original-image
/// <c>orig_im_size</c> and emits masks already scaled back to that resolution
/// — the trailing <c>Resize</c> op handles the inverse mapping. We only need
/// to pack pixels and pick the best mask.
/// </para>
/// <para>
/// <strong>Multi vs. single decoder.</strong> Both export shapes are
/// supported. The multi-mask decoder emits <c>[1, M, H, W]</c> (typically
/// M=4) candidate masks plus a per-mask predicted-IoU score; we
/// <c>argmax</c> over the IoU scores and return the most-confident mask.
/// The single-mask decoder emits one mask + one IoU score and the same
/// argmax-of-1 picks it trivially.
/// </para>
/// <para>
/// <strong>Output convention.</strong> SAM's mask output is a logit; we
/// threshold at zero (the model's training convention — positive = foreground)
/// and write a grayscale-as-RGBA bitmap. Equal-channel RGBA matches every
/// other image-emitting model so downstream consumers don't branch on colour
/// type. The output already matches the input's original W×H — no resize
/// pass on our side.
/// </para>
/// <para>
/// <strong>Per-row dispatch.</strong> Encoder input dims vary per row (no
/// fixed network resolution exposed on this side of the graph), so each row
/// runs its own encoder + decoder pass. The same trade-off as
/// <see cref="DepthEstimationModel"/> and <see cref="U2NetModel"/>; batching
/// would require padding to a common size and fixing up the decoder's
/// <c>orig_im_size</c> per row anyway.
/// </para>
/// </remarks>
public sealed class MobileSamModel : OnnxModel
{
    // samexporter convention: a single foreground point arrives as two
    // entries in (point_coords, point_labels) — the real prompt plus a
    // padding sentinel at (0, 0) with label -1 so the decoder's attention
    // mask treats it as ignored. Without the sentinel some decoder builds
    // expect at least two points and silently produce noise.
    private const int PromptPointCount = 2;

    // has_mask_input=0 disables the mask-conditioning path (we never feed
    // a prior mask in single-shot prompted segmentation). Held as a single
    // static buffer because the DenseTensor wrapper does not mutate it.
    private static readonly float[] HasMaskInputBuffer = [0f];

    // SAM expects the encoder's input to have its longest side resized to
    // 1024 before the graph zero-pads to 1024×1024. The samexporter
    // encoder graph performs only the padding step (Sub(1024, H/W) →
    // Pad), so we apply the resize on the C# side. The matching decoder's
    // orig_im_size logic likewise assumes longest-side=1024 (newH =
    // orig[0]*scale, newW = orig[1]*scale, scale = 1024/max(H,W)) for its
    // mask resize-back, so a full-size raw image fed straight in produces
    // a misaligned mask located at (x/scale, y/scale) of the output.
    private const float SamLongSideTarget = 1024f;

    private readonly InferenceSession _decoderSession;
    private readonly string _encoderInputName;
    private readonly string _encoderOutputName;

    // Decoder input/output names follow the samexporter convention. We
    // resolve them up-front and reuse — InputMetadata lookup is cheap but
    // happens per row otherwise.
    private const string DecoderImageEmbeddingsName = "image_embeddings";
    private const string DecoderPointCoordsName = "point_coords";
    private const string DecoderPointLabelsName = "point_labels";
    private const string DecoderMaskInputName = "mask_input";
    private const string DecoderHasMaskInputName = "has_mask_input";
    private const string DecoderOrigImSizeName = "orig_im_size";
    private const string DecoderMasksOutputName = "masks";
    private const string DecoderIouOutputName = "iou_predictions";

    /// <summary>
    /// Loads MobileSAM from a pair of ONNX files: the image encoder at
    /// <paramref name="encoderFilePath"/> and the mask decoder at
    /// <paramref name="decoderFilePath"/>. Validates the encoder declares the
    /// expected <c>[H, W, 3]</c> HWC float input and that the decoder
    /// declares the six-input prompt interface so a wrong-architecture file
    /// fails up-front.
    /// </summary>
    /// <param name="name">Catalog name this model is registered under.</param>
    /// <param name="encoderFilePath">Absolute path to the image-encoder ONNX file.</param>
    /// <param name="decoderFilePath">Absolute path to the mask-decoder ONNX file (single or multi).</param>
    public MobileSamModel(string name, string encoderFilePath, string decoderFilePath)
        : base(
            name,
            encoderFilePath,
            inputKinds: [DataKind.Image, DataKind.Float64, DataKind.Float64],
            outputKind: DataKind.Image,
            isDeterministic: true)
    {
        if (!File.Exists(decoderFilePath))
        {
            throw new FileNotFoundException(
                $"MobileSAM mask-decoder ONNX file not found at '{decoderFilePath}'. "
                + "MobileSamModel requires both an encoder and a decoder file.",
                decoderFilePath);
        }
        _decoderSession = OnnxSessionFactory.Create(decoderFilePath);

        if (Session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                $"MobileSamModel expected exactly one encoder input but '{encoderFilePath}' has "
                + $"{Session.InputMetadata.Count}: [{string.Join(", ", Session.InputMetadata.Keys)}].");
        }

        _encoderInputName = Session.InputMetadata.Keys.First();
        _encoderOutputName = Session.OutputMetadata.Keys.First();

        int[] encInputDims = Session.InputMetadata[_encoderInputName].Dimensions;
        if (encInputDims.Length != 3 || encInputDims[2] != 3)
        {
            throw new InvalidOperationException(
                $"MobileSamModel expected encoder input shape [H, W, 3] (samexporter convention) but '{encoderFilePath}' "
                + $"declares [{string.Join(", ", encInputDims)}]. Was the ONNX exported for a different SAM variant?");
        }

        // Verify the decoder exposes the six-input prompt interface. Wrong-
        // architecture files (e.g. an encoder file pointed at by mistake)
        // miss most of these, so checking the keys catches the mistake here
        // rather than as a NamedOnnxValue mismatch deep in InferSingleImage.
        string[] required =
        [
            DecoderImageEmbeddingsName, DecoderPointCoordsName, DecoderPointLabelsName,
            DecoderMaskInputName, DecoderHasMaskInputName, DecoderOrigImSizeName,
        ];
        foreach (string r in required)
        {
            if (!_decoderSession.InputMetadata.ContainsKey(r))
            {
                throw new InvalidOperationException(
                    $"MobileSAM decoder at '{decoderFilePath}' is missing the expected input '{r}'. "
                    + $"Found inputs: [{string.Join(", ", _decoderSession.InputMetadata.Keys)}]. "
                    + "Did the file export use a non-samexporter convention?");
            }
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        _ = overrides;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count == 0) return [];

        ValueRef[] results = new ValueRef[inputs.Count];

        for (int row = 0; row < inputs.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ValueRef> rowInputs = inputs[row];
            ValueRef image = rowInputs[0];
            ValueRef xRef = rowInputs[1];
            ValueRef yRef = rowInputs[2];
            if (image.IsNull || xRef.IsNull || yRef.IsNull)
            {
                throw new InvalidOperationException(
                    $"MobileSamModel received a null input at row {row}; filter nulls upstream before invoking the model.");
            }

            SKBitmap decoded = image.AsImage();
            float px = (float)xRef.AsFloat64();
            float py = (float)yRef.AsFloat64();

            results[row] = await Task.Run<ValueRef>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return InferSingleImage(decoded, px, py);
            }, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "MobileSamModel overrides InferBatchAsync directly because each row runs encoder + decoder against its own dims and prompt. BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "MobileSamModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    private ValueRef InferSingleImage(SKBitmap decoded, float promptX, float promptY)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        // 1. Resize to longest-side=1024 (aspect-preserving). The encoder's
        //    Pad-to-1024×1024 then plants the scaled image at the top-left
        //    of a 1024-square zero canvas, matching the input layout SAM
        //    was trained on.
        float scale = SamLongSideTarget / Math.Max(origW, origH);
        int resizedW = Math.Max(1, (int)MathF.Round(origW * scale));
        int resizedH = Math.Max(1, (int)MathF.Round(origH * scale));

        // 2. Pack the resized bitmap as HWC float32 in 0..255 RGB. The
        //    encoder bakes in the ImageNet (mean, std) normalise + the
        //    HWC→CHW transpose + the Pad-to-1024 — we hand it raw bytes.
        float[] hwcPixels = ResizeAndPackHwcRgb(decoded, resizedW, resizedH);
        DenseTensor<float> imageTensor = new(hwcPixels, [resizedH, resizedW, 3]);
        NamedOnnxValue encoderInput = OnnxTensorConversion.CreateAutoCastInput(
            Session, _encoderInputName, imageTensor);

        // 2. Encoder forward → image_embeddings [1, 256, 64, 64].
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoderOutputs =
            Session.Run([encoderInput]);
        DisposableNamedOnnxValue embeddingsValue = encoderOutputs.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "MobileSAM encoder ONNX session returned no outputs.");
        DenseTensor<float> embeddingsTensor = OnnxTensorConversion.ToFloatTensor(embeddingsValue);

        // 3. Build the decoder's six inputs. Coordinates must be in the
        //    same 1024-space the encoder operates in: the prompt point in
        //    original-image pixels gets the same `scale` applied as the
        //    image. The (0, 0) padding sentinel with label -1 follows the
        //    real prompt to satisfy decoders that expect ≥2 points.
        float[] pointCoords = [promptX * scale, promptY * scale, 0f, 0f];
        float[] pointLabels = [1f, -1f];
        DenseTensor<float> pointCoordsTensor = new(pointCoords, [1, PromptPointCount, 2]);
        DenseTensor<float> pointLabelsTensor = new(pointLabels, [1, PromptPointCount]);

        // No prior mask for first-pass prompted segmentation. Zero-filled
        // [1,1,256,256] + has_mask_input=0 disables the mask-conditioning path.
        float[] maskInputBuffer = new float[256 * 256];
        DenseTensor<float> maskInputTensor = new(maskInputBuffer, [1, 1, 256, 256]);
        DenseTensor<float> hasMaskInputTensor = new(HasMaskInputBuffer, [1]);

        // orig_im_size carries (H, W) — order matters; samexporter follows
        // the PyTorch (rows, cols) convention. Float32 (the decoder graph
        // declares orig_im_size as float, which surprises C# callers used
        // to seeing it as int64 in other SAM exports).
        DenseTensor<float> origImSizeTensor = new(new float[] { origH, origW }, [2]);

        NamedOnnxValue[] decoderInputs =
        [
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderImageEmbeddingsName, embeddingsTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderPointCoordsName, pointCoordsTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderPointLabelsName, pointLabelsTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderMaskInputName, maskInputTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderHasMaskInputName, hasMaskInputTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderOrigImSizeName, origImSizeTensor),
        ];

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decoderOutputs =
            _decoderSession.Run(decoderInputs);

        // 4. Pull masks + iou_predictions by name. The multi decoder emits
        //    [1, M, H, W] masks + [1, M] IoU scores; the single decoder
        //    emits [1, 1, H, W] + [1, 1]. argmax-of-N picks the highest-
        //    confidence mask in both shapes.
        DisposableNamedOnnxValue masksValue = decoderOutputs.FirstOrDefault(o => o.Name == DecoderMasksOutputName)
            ?? throw new InvalidOperationException(
                $"MobileSAM decoder did not return an output named '{DecoderMasksOutputName}'. "
                + $"Outputs: [{string.Join(", ", decoderOutputs.Select(o => o.Name))}].");
        DisposableNamedOnnxValue iouValue = decoderOutputs.FirstOrDefault(o => o.Name == DecoderIouOutputName)
            ?? throw new InvalidOperationException(
                $"MobileSAM decoder did not return an output named '{DecoderIouOutputName}'.");

        DenseTensor<float> masksTensor = OnnxTensorConversion.ToFloatTensor(masksValue);
        DenseTensor<float> iouTensor = OnnxTensorConversion.ToFloatTensor(iouValue);
        int[] maskDims = masksTensor.Dimensions.ToArray();

        if (maskDims.Length != 4 || maskDims[0] != 1)
        {
            throw new InvalidOperationException(
                $"MobileSAM masks output shape mismatch: expected [1, M, H, W], got "
                + $"[{string.Join(", ", maskDims)}].");
        }

        int candidateCount = maskDims[1];
        int maskH = maskDims[2];
        int maskW = maskDims[3];

        // The decoder's trailing Resize op should already produce masks at
        // (origH, origW). If a future export drops that op the caller would
        // see surprisingly small masks; assert here so the failure points
        // at the export rather than at downstream image consumers.
        if (maskH != origH || maskW != origW)
        {
            throw new InvalidOperationException(
                $"MobileSAM mask dims [{maskH},{maskW}] do not match input dims [{origH},{origW}]. "
                + "This export does not embed the resize-back op; use a samexporter build that includes orig_im_size handling.");
        }

        int bestIndex = 0;
        float bestScore = float.NegativeInfinity;
        ReadOnlySpan<float> iouSpan = iouTensor.Buffer.Span;
        for (int i = 0; i < candidateCount && i < iouSpan.Length; i++)
        {
            if (iouSpan[i] > bestScore)
            {
                bestScore = iouSpan[i];
                bestIndex = i;
            }
        }

        // 5. Threshold the chosen mask at zero (SAM logit convention) and
        //    write a grayscale-as-RGBA bitmap. Ownership transfers to the
        //    ValueRef — no `using` on the result.
        ReadOnlySpan<float> flat = masksTensor.Buffer.Span;
        int planeSize = origH * origW;
        int planeOffset = bestIndex * planeSize;

        SKImageInfo info = new(origW, origH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap mask = new(info);
        nint pixelPtr = mask.GetPixels();
        unsafe
        {
            byte* dst = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                byte g = flat[planeOffset + yx] > 0f ? (byte)255 : (byte)0;
                int o = yx * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }

        return ValueRef.FromImage(mask);
    }

    /// <summary>
    /// Aspect-preserving resize of <paramref name="source"/> to
    /// <paramref name="targetW"/>×<paramref name="targetH"/> packed as flat
    /// HWC float32 RGB in <c>[0, 255]</c>. The samexporter encoder expects
    /// raw pixel values; ImageNet normalisation, the HWC→CHW transpose,
    /// and the pad-to-1024×1024 happen inside the ONNX graph.
    /// </summary>
    private static float[] ResizeAndPackHwcRgb(SKBitmap source, int targetW, int targetH)
    {
        // Resize into a canonical Rgba8888 layout in one step. SkiaSharp's
        // platform-native colour type is BGRA on Windows; resizing into the
        // explicit RGBA target makes the channel order independent of host
        // platform, with no second copy.
        SKImageInfo info = new(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(info, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"SkiaSharp failed to resize {source.Width}×{source.Height} bitmap to "
                + $"{targetW}×{targetH} for MobileSAM input.");

        float[] hwc = new float[targetH * targetW * 3];
        nint ptr = resized.GetPixels();
        unsafe
        {
            byte* src = (byte*)ptr;
            int planeSize = targetH * targetW;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int sb = yx * 4;
                int db = yx * 3;
                hwc[db + 0] = src[sb + 0]; // R
                hwc[db + 1] = src[sb + 1]; // G
                hwc[db + 2] = src[sb + 2]; // B
            }
        }
        return hwc;
    }

    /// <inheritdoc />
    public new void Dispose()
    {
        _decoderSession.Dispose();
        base.Dispose();
    }
}
