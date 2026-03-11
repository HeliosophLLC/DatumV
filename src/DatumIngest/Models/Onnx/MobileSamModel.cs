using DatumIngest.Functions;
using DatumIngest.Model;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Whether <see cref="MobileSamModel"/> exposes prompted segmentation
/// (one mask per <c>(x, y)</c> click) or "everything" segmentation (one
/// mask per object found by sweeping a grid of point prompts and
/// deduplicating).
/// </summary>
public enum MobileSamMode
{
    /// <summary>
    /// SQL surface: <c>models.X(image, x, y) → Image</c>. Single
    /// foreground point in original-image pixel space; output is one
    /// binary mask of the object that contains the click.
    /// </summary>
    Prompted,

    /// <summary>
    /// SQL surface: <c>models.X(image, [gridSize]) → Array&lt;Image&gt;</c>.
    /// Sample a <c>gridSize × gridSize</c> grid of foreground prompts,
    /// run the decoder for each, filter low-quality candidates, NMS the
    /// rest, and return the survivors as an array of binary masks.
    /// </summary>
    Everything,
}

/// <summary>
/// MobileSAM segmentation — wraps the <c>vietanhdev/samexporter</c>
/// two-file ONNX export (<c>mobile_sam_image_encoder.onnx</c> +
/// <c>sam_mask_decoder_multi.onnx</c> or the single-mask variant) under
/// one model class. The exposed signature depends on
/// <see cref="MobileSamMode"/>: prompted segmentation takes
/// <c>(image, x, y)</c> and emits one mask; "everything" mode takes
/// <c>(image, [gridSize])</c> and emits an array of masks covering every
/// segmentable object the model finds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture.</strong> Two ONNX sessions form the pipeline:
/// the TinyViT image encoder (~28 MB) maps a per-row bitmap to a
/// <c>[1, 256, 64, 64]</c> embedding; the prompt-conditioned mask
/// decoder (~16 MB) consumes that embedding plus a single foreground
/// point and emits binary masks at the input image's original resolution.
/// The encoder is the heavy step (one pass per row); the decoder runs in
/// tens of milliseconds and is what we iterate in "everything" mode.
/// </para>
/// <para>
/// <strong>Encoder + decoder coordinate convention.</strong> The
/// samexporter encoder graph zero-pads its <c>[H, W, 3]</c> input to
/// 1024×1024 (top-left placement); the matching decoder's
/// <c>orig_im_size</c> resize-back assumes longest-side=1024. The model
/// class therefore pre-resizes the input to longest-side=1024 (with
/// <c>scale = 1024 / max(W, H)</c>) before the encoder pass and applies
/// the same scale to prompt coordinates. Without that, masks land at
/// <c>(x/scale, y/scale)</c> of the output instead of where the prompt
/// pointed.
/// </para>
/// <para>
/// <strong>Multi vs. single decoder.</strong> Both export shapes are
/// supported. The multi-mask decoder emits <c>[1, M, H, W]</c>
/// (typically <c>M = 4</c>) candidate masks plus a per-mask
/// predicted-IoU score. In prompted mode we <c>argmax</c> over IoU and
/// return the most-confident mask; in everything mode every candidate
/// (subject to filters) is a potential survivor.
/// </para>
/// <para>
/// <strong>Output convention.</strong> Each mask is a binary
/// grayscale-as-RGBA bitmap (white = foreground, black = background,
/// alpha = 255), sized to match the input image. Equal-channel RGBA
/// keeps the output uniform with U²-Net / depth maps so a single
/// <c>image_cutout(image, mask)</c> consumer handles all of them.
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

    // Everything-mode quality filters. SAM canonical defaults from the
    // automatic-mask-generator reference implementation; tightening
    // either drops more candidates and shrinks the output array.
    private const float PredIouThreshold = 0.88f;       // drop candidates the model is unsure about
    private const float StabilityScoreThreshold = 0.95f; // drop "fuzzy-edge" candidates
    private const float StabilityOffset = 1.0f;          // ±δ used when computing stability
    private const float NmsIouThreshold = 0.7f;          // dedup overlapping survivors
    private const int MinGridSize = 4;
    private const int MaxGridSize = 128;

    private readonly InferenceSession _decoderSession;
    private readonly string _encoderInputName;
    private readonly string _encoderOutputName;
    private readonly MobileSamMode _mode;
    private readonly int _defaultGridSize;

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
    /// <paramref name="decoderFilePath"/>. Validates that the encoder
    /// declares the expected <c>[H, W, 3]</c> HWC float input and that
    /// the decoder declares the six-input prompt interface so a wrong-
    /// architecture file fails up-front.
    /// </summary>
    /// <param name="name">Catalog name this model is registered under.</param>
    /// <param name="encoderFilePath">Absolute path to the image-encoder ONNX file.</param>
    /// <param name="decoderFilePath">Absolute path to the mask-decoder ONNX file (single or multi).</param>
    /// <param name="mode">Prompted (default) or Everything segmentation.</param>
    /// <param name="defaultGridSize">
    /// Grid side length for "everything" mode when the call site doesn't
    /// override. <c>32</c> means 1024 sample prompts, the SAM canonical
    /// default. Ignored in <see cref="MobileSamMode.Prompted"/> mode.
    /// </param>
    public MobileSamModel(
        string name,
        string encoderFilePath,
        string decoderFilePath,
        MobileSamMode mode = MobileSamMode.Prompted,
        int defaultGridSize = 32)
        : base(
            name,
            encoderFilePath,
            inputKinds: ResolveInputKinds(mode),
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
        if (mode == MobileSamMode.Everything && (defaultGridSize < MinGridSize || defaultGridSize > MaxGridSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultGridSize), defaultGridSize,
                $"defaultGridSize must be between {MinGridSize} and {MaxGridSize}.");
        }

        _mode = mode;
        _defaultGridSize = defaultGridSize;
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

        // Verify the decoder exposes the six-input prompt interface.
        // Wrong-architecture files (e.g. an encoder file pointed at by
        // mistake) miss most of these, so checking the keys catches the
        // mistake here rather than as a NamedOnnxValue mismatch deep in
        // the per-prompt dispatch.
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

    /// <summary>The mode this model instance is configured for.</summary>
    public MobileSamMode Mode => _mode;

    /// <summary>The default grid side-length used by <see cref="MobileSamMode.Everything"/> when no override is supplied.</summary>
    public int DefaultGridSize => _defaultGridSize;

    /// <inheritdoc />
    /// <remarks>
    /// In <see cref="MobileSamMode.Everything"/>, each row runs a
    /// <c>gridSize²</c> sweep of decoder calls — so a row with
    /// <c>gridSize = 32</c> takes ≈1k decoder dispatches. Setting
    /// <c>PreferredBatchSize = 1</c> surfaces results to the consumer
    /// row-by-row instead of waiting for the whole upstream batch, which
    /// makes streaming consumers usable. Prompted mode is fast enough
    /// that the default unbatched dispatch is fine.
    /// </remarks>
    public int? PreferredBatchSize => _mode == MobileSamMode.Everything ? 1 : null;

    private static IReadOnlyList<DataKind> ResolveInputKinds(MobileSamMode mode) => mode switch
    {
        MobileSamMode.Prompted => [DataKind.Image, DataKind.Float64, DataKind.Float64],
        MobileSamMode.Everything => [DataKind.Image],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown MobileSamMode '{mode}'."),
    };

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

            IReadOnlyList<ValueRef> rowInputs = inputs[row];
            ValueRef image = rowInputs[0];
            if (image.IsNull)
            {
                throw new InvalidOperationException(
                    $"MobileSamModel received a null image at row {row}; filter nulls upstream before invoking the model.");
            }
            SKBitmap decoded = image.AsImage();

            if (_mode == MobileSamMode.Prompted)
            {
                ValueRef xRef = rowInputs[1];
                ValueRef yRef = rowInputs[2];
                if (xRef.IsNull || yRef.IsNull)
                {
                    throw new InvalidOperationException(
                        $"MobileSamModel received a null prompt coordinate at row {row}; filter nulls upstream before invoking the model.");
                }
                float px = (float)xRef.AsFloat64();
                float py = (float)yRef.AsFloat64();
                results[row] = await Task.Run<ValueRef>(
                    () => InferPrompted(decoded, px, py, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int gridSize = ResolveGridSize(overrides, row);
                results[row] = await Task.Run<ValueRef>(
                    () => InferEverything(decoded, gridSize, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<NamedOnnxValue> BuildBatchInputs(
        IReadOnlyList<IReadOnlyList<ValueRef>> rows)
        => throw new InvalidOperationException(
            "MobileSamModel overrides InferBatchAsync directly because each row runs encoder + decoder against its own dims and prompt(s). BuildBatchInputs is not used.");

    /// <inheritdoc />
    protected override IReadOnlyList<ValueRef> ParseBatchOutputs(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        int batchSize)
        => throw new InvalidOperationException(
            "MobileSamModel overrides InferBatchAsync directly. ParseBatchOutputs is not used.");

    /// <summary>
    /// Reads the optional <c>gridSize</c> override declared in the
    /// catalog entry's <c>OptionalArgKinds</c>. Falls back to the
    /// construction-time default when the row carries no override or the
    /// value is null.
    /// </summary>
    private int ResolveGridSize(IReadOnlyList<IReadOnlyList<ValueRef>> overrides, int row)
    {
        if (overrides.Count <= row) return _defaultGridSize;
        IReadOnlyList<ValueRef> rowOverrides = overrides[row];
        if (rowOverrides.Count == 0 || rowOverrides[0].IsNull) return _defaultGridSize;

        int v = rowOverrides[0].ToInt32();
        if (v < MinGridSize || v > MaxGridSize)
        {
            throw new ArgumentOutOfRangeException(
                "gridSize", v,
                $"MobileSAM gridSize override must be between {MinGridSize} and {MaxGridSize} (got {v}). "
                + $"A {v}×{v} grid would generate {(long)v * v} prompts.");
        }
        return v;
    }

    private ValueRef InferPrompted(SKBitmap decoded, float promptX, float promptY, CancellationToken ct)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        EncoderResult enc = RunEncoder(decoded);

        // Coordinates must be in the same 1024-space the encoder pass
        // uses; the prompt point in original-image pixels gets the same
        // `scale` applied. The (0, 0) padding sentinel with label -1
        // follows the real prompt to satisfy decoders that expect ≥ 2
        // points.
        float[] pointCoords = [promptX * enc.Scale, promptY * enc.Scale, 0f, 0f];
        float[] pointLabels = [1f, -1f];

        DecoderResult dec = RunDecoder(enc, pointCoords, pointLabels, origH, origW);

        // Argmax across candidate masks by predicted IoU.
        int bestIndex = 0;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < dec.CandidateCount; i++)
        {
            if (dec.IouScores[i] > bestScore)
            {
                bestScore = dec.IouScores[i];
                bestIndex = i;
            }
        }

        ct.ThrowIfCancellationRequested();
        return ThresholdToBitmapValueRef(dec.Logits, bestIndex, dec.PlaneSize, origW, origH);
    }

    private ValueRef InferEverything(SKBitmap decoded, int gridSize, CancellationToken ct)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        EncoderResult enc = RunEncoder(decoded);

        // Sample one foreground prompt at the centre of each grid cell —
        // SAM's automatic-mask-generator convention. Each prompt yields
        // up to four candidate masks; filtering happens per-candidate.
        List<MaskCandidate> candidates = new();
        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                ct.ThrowIfCancellationRequested();

                float px = (gx + 0.5f) * origW / gridSize;
                float py = (gy + 0.5f) * origH / gridSize;
                float[] pointCoords = [px * enc.Scale, py * enc.Scale, 0f, 0f];
                float[] pointLabels = [1f, -1f];

                DecoderResult dec = RunDecoder(enc, pointCoords, pointLabels, origH, origW);

                for (int c = 0; c < dec.CandidateCount; c++)
                {
                    float iou = dec.IouScores[c];
                    if (iou < PredIouThreshold) continue;

                    int planeOffset = c * dec.PlaneSize;
                    float stability = ComputeStabilityScore(
                        dec.Logits, planeOffset, dec.PlaneSize, StabilityOffset);
                    if (stability < StabilityScoreThreshold) continue;

                    byte[] maskBytes = ThresholdToBytes(dec.Logits, planeOffset, dec.PlaneSize);
                    int area = CountForeground(maskBytes);
                    if (area == 0) continue;

                    candidates.Add(new MaskCandidate(maskBytes, area, iou));
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        // NMS by mask IoU. Higher predicted-IoU wins; overlapping
        // candidates below the kept one are suppressed.
        List<MaskCandidate> survivors = NonMaxSuppress(candidates, NmsIouThreshold);

        ValueRef[] maskRefs = new ValueRef[survivors.Count];
        for (int i = 0; i < survivors.Count; i++)
        {
            maskRefs[i] = BytesToBitmapValueRef(survivors[i].MaskBytes, origW, origH);
        }
        return ValueRef.FromArray(DataKind.Image, maskRefs);
    }

    /// <summary>
    /// Encodes a row's bitmap into a 1024-aware embedding tensor. The
    /// returned <see cref="EncoderResult.OnnxOutputs"/> must be disposed
    /// after the embedding is no longer needed (the embedding tensor's
    /// buffer is shared with the disposable collection).
    /// </summary>
    private EncoderResult RunEncoder(SKBitmap decoded)
    {
        int origW = decoded.Width;
        int origH = decoded.Height;

        // Pre-resize to longest-side=1024 — the encoder's Pad-to-1024×1024
        // then plants the scaled image at the top-left of a 1024-square
        // zero canvas, matching the input layout SAM was trained on.
        float scale = SamLongSideTarget / Math.Max(origW, origH);
        int resizedW = Math.Max(1, (int)MathF.Round(origW * scale));
        int resizedH = Math.Max(1, (int)MathF.Round(origH * scale));

        float[] hwcPixels = ResizeAndPackHwcRgb(decoded, resizedW, resizedH);
        DenseTensor<float> imageTensor = new(hwcPixels, [resizedH, resizedW, 3]);
        NamedOnnxValue encoderInput = OnnxTensorConversion.CreateAutoCastInput(
            Session, _encoderInputName, imageTensor);

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = Session.Run([encoderInput]);
        DisposableNamedOnnxValue embeddingsValue = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("MobileSAM encoder ONNX session returned no outputs.");
        DenseTensor<float> embeddings = OnnxTensorConversion.ToFloatTensor(embeddingsValue);

        return new EncoderResult(outputs, embeddings, scale);
    }

    /// <summary>
    /// Runs the prompt-conditioned mask decoder for one prompt and
    /// returns the candidate masks (logits, not binary) along with their
    /// predicted-IoU scores. Caller chooses how to consume them
    /// (argmax-of-N for prompted, all-of-N + filters for everything mode).
    /// </summary>
    private DecoderResult RunDecoder(
        EncoderResult enc, float[] pointCoords, float[] pointLabels, int origH, int origW)
    {
        DenseTensor<float> pointCoordsTensor = new(pointCoords, [1, PromptPointCount, 2]);
        DenseTensor<float> pointLabelsTensor = new(pointLabels, [1, PromptPointCount]);

        // No prior mask — has_mask_input=0 disables the conditioning path.
        float[] maskInputBuffer = new float[256 * 256];
        DenseTensor<float> maskInputTensor = new(maskInputBuffer, [1, 1, 256, 256]);
        DenseTensor<float> hasMaskInputTensor = new(HasMaskInputBuffer, [1]);

        // orig_im_size carries (H, W) — order matters; samexporter follows
        // the PyTorch (rows, cols) convention.
        DenseTensor<float> origImSizeTensor = new(new float[] { origH, origW }, [2]);

        NamedOnnxValue[] decoderInputs =
        [
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderImageEmbeddingsName, enc.Embeddings),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderPointCoordsName, pointCoordsTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderPointLabelsName, pointLabelsTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderMaskInputName, maskInputTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderHasMaskInputName, hasMaskInputTensor),
            OnnxTensorConversion.CreateAutoCastInput(_decoderSession, DecoderOrigImSizeName, origImSizeTensor),
        ];

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            _decoderSession.Run(decoderInputs);

        DisposableNamedOnnxValue masksValue = outputs.FirstOrDefault(o => o.Name == DecoderMasksOutputName)
            ?? throw new InvalidOperationException(
                $"MobileSAM decoder did not return an output named '{DecoderMasksOutputName}'. "
                + $"Outputs: [{string.Join(", ", outputs.Select(o => o.Name))}].");
        DisposableNamedOnnxValue iouValue = outputs.FirstOrDefault(o => o.Name == DecoderIouOutputName)
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

        // The decoder's trailing Resize op should already produce masks
        // at (origH, origW). If a future export drops that op the caller
        // would see surprisingly small masks; assert here so the failure
        // points at the export rather than at downstream image consumers.
        if (maskH != origH || maskW != origW)
        {
            throw new InvalidOperationException(
                $"MobileSAM mask dims [{maskH},{maskW}] do not match input dims [{origH},{origW}]. "
                + "This export does not embed the resize-back op; use a samexporter build that includes orig_im_size handling.");
        }

        // Copy buffers out of the disposable collection — the collection
        // is disposed when this method returns, taking the underlying
        // tensor buffers with it.
        float[] logits = masksTensor.Buffer.Span.ToArray();
        float[] iouScores = new float[candidateCount];
        ReadOnlySpan<float> iouSpan = iouTensor.Buffer.Span;
        for (int i = 0; i < candidateCount && i < iouSpan.Length; i++)
        {
            iouScores[i] = iouSpan[i];
        }

        return new DecoderResult(logits, iouScores, candidateCount, maskH * maskW);
    }

    /// <summary>
    /// SAM's stability score: IoU between the binary mask thresholded at
    /// <c>+δ</c> versus <c>−δ</c>. Crisp boundaries push this near 1.0;
    /// fuzzy edges drag it toward 0. The standard threshold (0.95) drops
    /// "noise" candidates whose foreground/background distinction is
    /// unstable to small logit perturbations.
    /// </summary>
    private static float ComputeStabilityScore(float[] logits, int offset, int planeSize, float delta)
    {
        int intersection = 0;
        int union = 0;
        for (int p = 0; p < planeSize; p++)
        {
            float v = logits[offset + p];
            bool high = v > +delta;   // foreground at the strict (+δ) threshold
            bool low = v > -delta;    // foreground at the loose (−δ) threshold
            if (high && low) intersection++;
            if (high || low) union++;
        }
        if (union == 0) return 0f;
        return (float)intersection / union;
    }

    /// <summary>
    /// Standard mask-IoU NMS: sort by score descending, walk the list
    /// keeping each entry, and suppress every later entry whose mask
    /// overlaps the kept one above <paramref name="iouThreshold"/>. The
    /// result is the deduplicated set of "everything mode" segments.
    /// </summary>
    private static List<MaskCandidate> NonMaxSuppress(List<MaskCandidate> candidates, float iouThreshold)
    {
        if (candidates.Count <= 1) return new List<MaskCandidate>(candidates);

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        bool[] suppressed = new bool[candidates.Count];
        List<MaskCandidate> keep = new();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i]) continue;
            MaskCandidate kept = candidates[i];
            keep.Add(kept);
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j]) continue;
                if (MaskIou(kept, candidates[j]) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }
        return keep;
    }

    private static float MaskIou(in MaskCandidate a, in MaskCandidate b)
    {
        byte[] am = a.MaskBytes;
        byte[] bm = b.MaskBytes;
        if (am.Length != bm.Length) return 0f;

        int intersection = 0;
        int union = 0;
        for (int i = 0; i < am.Length; i++)
        {
            bool af = am[i] != 0;
            bool bf = bm[i] != 0;
            if (af && bf) intersection++;
            if (af || bf) union++;
        }
        if (union == 0) return 0f;
        return (float)intersection / union;
    }

    private static byte[] ThresholdToBytes(float[] logits, int offset, int planeSize)
    {
        byte[] bytes = new byte[planeSize];
        for (int p = 0; p < planeSize; p++)
        {
            bytes[p] = logits[offset + p] > 0f ? (byte)1 : (byte)0;
        }
        return bytes;
    }

    private static int CountForeground(byte[] mask)
    {
        int count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != 0) count++;
        }
        return count;
    }

    private static ValueRef BytesToBitmapValueRef(byte[] mask, int width, int height)
    {
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap bitmap = new(info);
        nint pixelPtr = bitmap.GetPixels();
        unsafe
        {
            byte* dst = (byte*)pixelPtr;
            for (int i = 0; i < mask.Length; i++)
            {
                byte g = mask[i] != 0 ? (byte)255 : (byte)0;
                int o = i * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }
        return ValueRef.FromImage(bitmap);
    }

    private static ValueRef ThresholdToBitmapValueRef(float[] logits, int candidateIndex, int planeSize, int width, int height)
    {
        int offset = candidateIndex * planeSize;
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap bitmap = new(info);
        nint pixelPtr = bitmap.GetPixels();
        unsafe
        {
            byte* dst = (byte*)pixelPtr;
            for (int p = 0; p < planeSize; p++)
            {
                byte g = logits[offset + p] > 0f ? (byte)255 : (byte)0;
                int o = p * 4;
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }
        return ValueRef.FromImage(bitmap);
    }

    /// <summary>
    /// Aspect-preserving resize of <paramref name="source"/> to
    /// <paramref name="targetW"/>×<paramref name="targetH"/> packed as
    /// flat HWC float32 RGB in <c>[0, 255]</c>. The samexporter encoder
    /// expects raw pixel values; ImageNet normalisation, the HWC→CHW
    /// transpose, and the pad-to-1024×1024 happen inside the ONNX graph.
    /// </summary>
    private static float[] ResizeAndPackHwcRgb(SKBitmap source, int targetW, int targetH)
    {
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

    /// <summary>
    /// Encoder pass output: the embedding tensor (referenced by the
    /// decoder calls that follow) plus the longest-side scale that maps
    /// original-image coordinates into the encoder's 1024-space. The
    /// <see cref="OnnxOutputs"/> disposable owns the embedding's
    /// underlying buffer — keep it alive for the duration of every
    /// decoder call that consumes the embedding.
    /// </summary>
    private readonly struct EncoderResult : IDisposable
    {
        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> OnnxOutputs { get; }
        public DenseTensor<float> Embeddings { get; }
        public float Scale { get; }

        public EncoderResult(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> onnxOutputs,
            DenseTensor<float> embeddings,
            float scale)
        {
            OnnxOutputs = onnxOutputs;
            Embeddings = embeddings;
            Scale = scale;
        }

        public void Dispose() => OnnxOutputs.Dispose();
    }

    /// <summary>
    /// Decoder pass output: per-candidate logits (flattened as
    /// <c>[CandidateCount, planeSize]</c> in the <see cref="Logits"/>
    /// buffer) plus the per-candidate predicted-IoU scores. Logits are
    /// thresholded at <c>0</c> to produce the final binary mask.
    /// </summary>
    private readonly struct DecoderResult
    {
        public float[] Logits { get; }
        public float[] IouScores { get; }
        public int CandidateCount { get; }
        public int PlaneSize { get; }

        public DecoderResult(float[] logits, float[] iouScores, int candidateCount, int planeSize)
        {
            Logits = logits;
            IouScores = iouScores;
            CandidateCount = candidateCount;
            PlaneSize = planeSize;
        }
    }

    /// <summary>
    /// One mask candidate inside "everything" mode after IoU + stability
    /// filtering, before NMS. Stored as a packed 0/non-zero byte buffer
    /// at original-image dims so mask-IoU computations between pairs run
    /// without re-thresholding the logits.
    /// </summary>
    private readonly struct MaskCandidate
    {
        public byte[] MaskBytes { get; }
        public int Area { get; }
        public float Score { get; }

        public MaskCandidate(byte[] maskBytes, int area, float score)
        {
            MaskBytes = maskBytes;
            Area = area;
            Score = score;
        }
    }
}
