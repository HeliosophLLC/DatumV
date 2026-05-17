using DatumIngest.Functions.Image;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// <c>image_tile(img, width, height)</c> — packs the images in a group into a
/// fixed-size canvas, flowing left-to-right and wrapping to a new row when the
/// next image would overflow horizontally. Each row's height is the max height
/// of the images placed on it; images shorter than the row are centred
/// vertically within the row band. The output canvas is always
/// <c>width × height</c>; unfilled regions are transparent. Tiling stops when
/// the next image (or its row, if it expands the row height) would overflow
/// the canvas vertically — remaining images are dropped silently. Images wider
/// than the canvas are skipped. Supports intra-aggregate <c>ORDER BY</c> to
/// control tile order: <c>image_tile(img, 1024, 1024 ORDER BY idx)</c>.
/// </summary>
/// <remarks>
/// Encoded bytes are copied into managed memory on accumulate (disconnected
/// from the per-row arena); decode + canvas composition happen once at
/// finalisation. Output is PNG-encoded with width/height/channels stamped
/// inline via <see cref="ImageDataValueFactory.FromBitmap"/>.
/// </remarks>
public sealed class ImageTileAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "image_tile";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Packs images into a fixed width × height canvas, flowing left-to-right with row wrap; supports WITHIN GROUP (ORDER BY ...).";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("width",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc/>
    public WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.SortModifier;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
        {
            throw new ArgumentException(
                "image_tile() requires exactly three arguments: image, width, height.");
        }
        if (argumentKinds[0] != DataKind.Image)
        {
            throw new ArgumentException(
                $"image_tile() first argument must be Image, got {argumentKinds[0]}.");
        }
        if (!IsIntegerKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"image_tile() second argument (width) must be an integer, got {argumentKinds[1]}.");
        }
        if (!IsIntegerKind(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"image_tile() third argument (height) must be an integer, got {argumentKinds[2]}.");
        }
        return DataKind.Image;
    }

    private static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64;

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Image);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<byte[]> _images = [];
        private int? _canvasWidth;
        private int? _canvasHeight;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (_canvasWidth is null && !arguments[1].IsNull)
            {
                _canvasWidth = ReadInt(arguments[1]);
            }
            if (_canvasHeight is null && !arguments[2].IsNull)
            {
                _canvasHeight = ReadInt(arguments[2]);
            }

            if (arguments[0].IsNull) return;

            byte[] bytes = arguments[0].AsImage(frame.Source, frame.SidecarRegistry);
            _images.Add(bytes);
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            _images.AddRange(o._images);
            _canvasWidth ??= o._canvasWidth;
            _canvasHeight ??= o._canvasHeight;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_images.Count == 0)
            {
                return new ValueTask<DataValue>(DataValue.Null(DataKind.Image));
            }
            if (_canvasWidth is null || _canvasHeight is null)
            {
                throw new FunctionArgumentException("image_tile",
                    "width/height were null for every accumulated row; "
                    + "pass positive integer canvas dimensions.");
            }
            if (_canvasWidth.Value <= 0 || _canvasHeight.Value <= 0)
            {
                throw new FunctionArgumentException("image_tile",
                    $"width and height must be positive (got {_canvasWidth.Value}×{_canvasHeight.Value}).");
            }

            int canvasW = _canvasWidth.Value;
            int canvasH = _canvasHeight.Value;
            SKBitmap[] bitmaps = new SKBitmap[_images.Count];
            try
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    bitmaps[i] = SKBitmap.Decode(_images[i])
                        ?? throw new FunctionArgumentException("image_tile",
                            $"failed to decode image at index {i}.");
                }

                SKImageInfo info = new(
                    canvasW, canvasH,
                    SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using SKBitmap output = new(info);
                using (SKCanvas canvas = new(output))
                {
                    canvas.Clear(SKColors.Transparent);
                    LayoutAndDraw(bitmaps, canvasW, canvasH, canvas);
                }

                return new ValueTask<DataValue>(
                    ImageDataValueFactory.FromBitmap(output, frame.Target));
            }
            finally
            {
                for (int i = 0; i < bitmaps.Length; i++)
                {
                    bitmaps[i]?.Dispose();
                }
            }
        }

        private static void LayoutAndDraw(SKBitmap[] bitmaps, int canvasW, int canvasH, SKCanvas canvas)
        {
            List<(SKBitmap bmp, int x)> currentRow = [];
            int currentRowMaxHeight = 0;
            int currentRowOffsetX = 0;
            int currentY = 0;

            void FlushRow()
            {
                foreach ((SKBitmap bmp, int x) in currentRow)
                {
                    int y = currentY + (currentRowMaxHeight - bmp.Height) / 2;
                    canvas.DrawBitmap(bmp, x, y);
                }
                currentY += currentRowMaxHeight;
                currentRow.Clear();
                currentRowMaxHeight = 0;
                currentRowOffsetX = 0;
            }

            foreach (SKBitmap bmp in bitmaps)
            {
                if (bmp.Width > canvasW) continue;

                if (currentRowOffsetX + bmp.Width > canvasW)
                {
                    FlushRow();
                }

                int rowHeightWithThis = System.Math.Max(currentRowMaxHeight, bmp.Height);
                if (currentY + rowHeightWithThis > canvasH)
                {
                    break;
                }

                currentRow.Add((bmp, currentRowOffsetX));
                currentRowOffsetX += bmp.Width;
                currentRowMaxHeight = rowHeightWithThis;
            }

            FlushRow();
        }

        public void Reset()
        {
            _images.Clear();
            _canvasWidth = null;
            _canvasHeight = null;
        }

        private static int ReadInt(DataValue value) => value.Kind switch
        {
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.Int64 => checked((int)value.AsInt64()),
            _ => throw new FunctionArgumentException("image_tile",
                $"expected integer, got {value.Kind}."),
        };
    }
}
