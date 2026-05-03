using DatumIngest.Functions.Image;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// <c>image_stack(img, axis)</c> — concatenates the images in a group along
/// the requested axis. <c>axis</c> is a case-insensitive string enum:
/// <c>'horizontal'</c> stacks left-to-right (output width = sum of widths,
/// height = max of heights), <c>'vertical'</c> stacks top-to-bottom (width =
/// max, height = sum). Mismatched perpendicular sizes are centred and the
/// excess padded with transparent pixels. Supports intra-aggregate
/// <c>ORDER BY</c> to control stack order:
/// <c>image_stack(img, 'horizontal' ORDER BY position)</c>.
/// </summary>
/// <remarks>
/// Encoded bytes are copied into managed memory on accumulate (disconnected
/// from the per-row arena); decode + canvas composition happen once at
/// finalisation. Output is PNG-encoded with width/height/channels stamped
/// inline via <see cref="ImageDataValueFactory.FromBitmap"/>.
/// </remarks>
public sealed class ImageStackAggregateFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "image_stack";

    /// <inheritdoc/>
    public WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.SortModifier;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "image_stack() requires exactly two arguments: image and axis.");
        }
        if (argumentKinds[0] != DataKind.Image)
        {
            throw new ArgumentException(
                $"image_stack() first argument must be Image, got {argumentKinds[0]}.");
        }
        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"image_stack() second argument (axis) must be String, got {argumentKinds[1]}.");
        }
        return DataKind.Image;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Image);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    private enum StackAxis
    {
        Horizontal,
        Vertical,
    }

    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<byte[]> _images = [];
        private StackAxis? _axis;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (_axis is null && !arguments[1].IsNull)
            {
                string raw = arguments[1].AsString(frame.Source);
                _axis = ParseAxis(raw);
            }

            if (arguments[0].IsNull) return;

            byte[] bytes = arguments[0].AsImage(frame.Source, frame.SidecarRegistry);
            _images.Add(bytes);
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            _images.AddRange(o._images);
            _axis ??= o._axis;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_images.Count == 0)
            {
                return new ValueTask<DataValue>(DataValue.Null(DataKind.Image));
            }
            if (_axis is null)
            {
                throw new FunctionArgumentException("image_stack",
                    "axis was null for every accumulated row; "
                    + "pass 'horizontal' or 'vertical'.");
            }

            StackAxis axis = _axis.Value;
            SKBitmap[] bitmaps = new SKBitmap[_images.Count];
            try
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    bitmaps[i] = SKBitmap.Decode(_images[i])
                        ?? throw new FunctionArgumentException("image_stack",
                            $"failed to decode image at index {i}.");
                }

                int outWidth = 0;
                int outHeight = 0;
                if (axis == StackAxis.Horizontal)
                {
                    foreach (SKBitmap bmp in bitmaps)
                    {
                        outWidth += bmp.Width;
                        if (bmp.Height > outHeight) outHeight = bmp.Height;
                    }
                }
                else
                {
                    foreach (SKBitmap bmp in bitmaps)
                    {
                        if (bmp.Width > outWidth) outWidth = bmp.Width;
                        outHeight += bmp.Height;
                    }
                }

                SKImageInfo info = new(
                    outWidth, outHeight,
                    SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using SKBitmap output = new(info);
                using (SKCanvas canvas = new(output))
                {
                    canvas.Clear(SKColors.Transparent);
                    int offset = 0;
                    foreach (SKBitmap bmp in bitmaps)
                    {
                        if (axis == StackAxis.Horizontal)
                        {
                            int y = (outHeight - bmp.Height) / 2;
                            canvas.DrawBitmap(bmp, offset, y);
                            offset += bmp.Width;
                        }
                        else
                        {
                            int x = (outWidth - bmp.Width) / 2;
                            canvas.DrawBitmap(bmp, x, offset);
                            offset += bmp.Height;
                        }
                    }
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

        public void Reset()
        {
            _images.Clear();
            _axis = null;
        }

        private static StackAxis ParseAxis(string raw) => raw.ToLowerInvariant() switch
        {
            "horizontal" or "h" => StackAxis.Horizontal,
            "vertical" or "v" => StackAxis.Vertical,
            _ => throw new FunctionArgumentException("image_stack",
                $"unknown axis '{raw}'. Supported: horizontal, vertical."),
        };
    }
}
