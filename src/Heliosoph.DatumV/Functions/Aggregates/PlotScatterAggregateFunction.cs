using Heliosoph.DatumV.Functions.Image;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>plot_scatter_agg(x, y [, class] [, options STRUCT]) → Image</c> — renders the
/// group's (x, y) points as a scatter plot. Coordinates are autoscaled to the
/// canvas with symmetric padding; the optional integer <c>class</c> selects a
/// point color from a built-in categorical palette, so cluster ids from
/// <c>nearest_centroid</c> color the plot directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Options.</strong> The options struct accepts any of: <c>width</c>
/// (int, default 1200), <c>height</c> (int, default 630), <c>background</c>
/// (Color, default transparent), <c>point_size</c> (float radius in pixels,
/// default 4), <c>padding</c> (float fraction of the data range added on each
/// side, default 0.05). Unrecognized fields are ignored. Options are captured
/// from the first row and must be constant across the group.
/// </para>
/// <para>
/// <strong>Scaling.</strong> Each axis maps its padded data range onto the
/// canvas; y increases upward (screen-inverted). A zero-range axis (single
/// point, or all values equal) expands to ±1 around the value so the points
/// center rather than degenerate.
/// </para>
/// <para>
/// <strong>Palette.</strong> Ten categorical colors (Tableau-10). The class id
/// indexes the palette modulo 10; rows without a class (or with a null class)
/// use the first entry. Class ids are opaque labels — matching
/// <c>kmeans_fit_agg</c>'s contract that centroid order carries no meaning.
/// </para>
/// <para>
/// <strong>Semantics.</strong> Rows with a null or non-finite x or y are
/// skipped. Groups with no drawable points return NULL. Points draw in
/// accumulation order (later rows paint over earlier ones). Merge concatenates
/// point lists, so parallel hash-aggregate merging is supported. Output is a
/// PNG-encoded Image.
/// </para>
/// </remarks>
public sealed class PlotScatterAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    private const int DefaultWidth = 1200;
    private const int DefaultHeight = 630;
    private const int MaxDimension = 16384;
    private const float DefaultPointSize = 4f;
    private const float DefaultPadding = 0.05f;

    /// <summary>Tableau-10 categorical palette; class ids index modulo 10.</summary>
    private static readonly SKColor[] Palette =
    [
        new(0x4E, 0x79, 0xA7),
        new(0xF2, 0x8E, 0x2B),
        new(0xE1, 0x57, 0x59),
        new(0x76, 0xB7, 0xB2),
        new(0x59, 0xA1, 0x4D),
        new(0xED, 0xC9, 0x48),
        new(0xB0, 0x7A, 0xA1),
        new(0xFF, 0x9D, 0xA7),
        new(0x9C, 0x75, 0x5F),
        new(0xBA, 0xB0, 0xAC),
    ];

    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "plot_scatter_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Renders the group's (x, y) points as a scatter-plot Image with padded autoscaling: "
        + "plot_scatter_agg(x, y [, class] [, {width, height, background, point_size, padding}]) → Image. "
        + "The integer class picks a categorical palette color — feed nearest_centroid cluster ids directly.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("class", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("options", DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("class", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("options", DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 2 or > 4)
        {
            throw new ArgumentException(
                "plot_scatter_agg() requires two to four arguments: x, y, optional class, optional options struct.");
        }
        if (!PercentileDiscreteFunction.IsNumericKind(argumentKinds[0])
            || !PercentileDiscreteFunction.IsNumericKind(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"plot_scatter_agg() x and y must be numeric, got {argumentKinds[0]} and {argumentKinds[1]}.");
        }
        if (argumentKinds.Length == 3
            && argumentKinds[2] != DataKind.Struct
            && !DataKindFamily.IntegerFamily.Contains(argumentKinds[2]))
        {
            throw new ArgumentException(
                $"plot_scatter_agg() third argument must be an integer class or an options struct, got {argumentKinds[2]}.");
        }
        if (argumentKinds.Length == 4)
        {
            if (!DataKindFamily.IntegerFamily.Contains(argumentKinds[2]))
            {
                throw new ArgumentException(
                    $"plot_scatter_agg() third argument (class) must be an integer, got {argumentKinds[2]}.");
            }
            if (argumentKinds[3] != DataKind.Struct)
            {
                throw new ArgumentException(
                    $"plot_scatter_agg() fourth argument (options) must be a struct, got {argumentKinds[3]}.");
            }
        }
        return DataKind.Image;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Image);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Collects (x, y, class) triples in managed memory; scaling and
    /// rasterization run once at <see cref="ResultAsync"/>.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<(double X, double Y, int Cls)> _points = [];
        private int _width = DefaultWidth;
        private int _height = DefaultHeight;
        private SKColor? _background;
        private float _pointSize = DefaultPointSize;
        private float _padding = DefaultPadding;
        private bool _optionsCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            // Layout: [x, y], [x, y, class], [x, y, options], [x, y, class, options].
            int classIndex = -1;
            int optionsIndex = -1;
            if (arguments.Length == 3)
            {
                if (arguments[2].Kind == DataKind.Struct) optionsIndex = 2;
                else classIndex = 2;
            }
            else if (arguments.Length == 4)
            {
                classIndex = 2;
                optionsIndex = 3;
            }

            if (optionsIndex >= 0 && !_optionsCaptured && !arguments[optionsIndex].IsNull)
            {
                CaptureOptions(arguments[optionsIndex], frame);
            }

            if (arguments[0].IsNull || arguments[1].IsNull)
            {
                return;
            }

            double x = PercentileDiscreteFunction.ToDouble(arguments[0]);
            double y = PercentileDiscreteFunction.ToDouble(arguments[1]);
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return;
            }

            int cls = 0;
            if (classIndex >= 0 && !arguments[classIndex].IsNull
                && arguments[classIndex].TryToFloat(out float clsFloat))
            {
                cls = (int)clsFloat;
            }

            _points.Add((x, y, cls));
        }

        private void CaptureOptions(in DataValue optionsArg, in InvocationFrame frame)
        {
            _optionsCaptured = true;

            TypeDescriptor? descriptor = frame.Types?.GetDescriptor(optionsArg.TypeId);
            if (descriptor?.Fields is null)
            {
                throw new FunctionArgumentException(Name,
                    "options struct has no registered field descriptors; expected a struct literal "
                    + "with any of width, height, background, point_size, padding.");
            }

            DataValue[] fields = optionsArg.AsStruct(frame.Source);
            for (int i = 0; i < descriptor.Fields.Count && i < fields.Length; i++)
            {
                if (fields[i].IsNull) continue;

                switch (descriptor.Fields[i].Name.ToLowerInvariant())
                {
                    case "width":
                        _width = ReadDimension(fields[i], "width");
                        break;
                    case "height":
                        _height = ReadDimension(fields[i], "height");
                        break;
                    case "background":
                        if (fields[i].Kind != DataKind.Color)
                        {
                            throw new FunctionArgumentException(Name,
                                $"options.background must be a Color (use color() or color_hex()), got {fields[i].Kind}.");
                        }
                        (byte r, byte g, byte b, byte a) = fields[i].AsColor();
                        _background = new SKColor(r, g, b, a);
                        break;
                    case "point_size":
                        if (!fields[i].TryToFloat(out float size) || size <= 0)
                        {
                            throw new FunctionArgumentException(Name,
                                "options.point_size must be a positive number.");
                        }
                        _pointSize = size;
                        break;
                    case "padding":
                        if (!fields[i].TryToFloat(out float padding) || padding < 0)
                        {
                            throw new FunctionArgumentException(Name,
                                "options.padding must be a non-negative number.");
                        }
                        _padding = padding;
                        break;
                }
            }
        }

        private static int ReadDimension(in DataValue field, string name)
        {
            if (!field.TryToFloat(out float value))
            {
                throw new FunctionArgumentException(Name,
                    $"options.{name} must be an integer, got a {field.Kind} value.");
            }
            int dimension = (int)value;
            if (dimension < 1 || dimension > MaxDimension)
            {
                throw new FunctionArgumentException(Name,
                    $"options.{name} must be between 1 and {MaxDimension}, got {dimension}.");
            }
            return dimension;
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            if (o._optionsCaptured && !_optionsCaptured)
            {
                _width = o._width;
                _height = o._height;
                _background = o._background;
                _pointSize = o._pointSize;
                _padding = o._padding;
                _optionsCaptured = true;
            }
            _points.AddRange(o._points);
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_points.Count == 0)
            {
                return new(DataValue.Null(DataKind.Image));
            }

            var (x0, x1) = PaddedDomain(_points.Min(p => p.X), _points.Max(p => p.X));
            var (y0, y1) = PaddedDomain(_points.Min(p => p.Y), _points.Max(p => p.Y));

            SKImageInfo info = new(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKBitmap bitmap = new(info);
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(_background ?? SKColors.Transparent);
                using SKPaint paint = new();
                paint.IsAntialias = true;
                paint.Style = SKPaintStyle.Fill;

                foreach ((double x, double y, int cls) in _points)
                {
                    float px = (float)((x - x0) / (x1 - x0) * (_width - 1));
                    float py = (float)((_height - 1) - (y - y0) / (y1 - y0) * (_height - 1));
                    paint.Color = Palette[((cls % Palette.Length) + Palette.Length) % Palette.Length];
                    canvas.DrawCircle(px, py, _pointSize, paint);
                }
            }

            return new(ImageDataValueFactory.FromBitmap(bitmap, frame.Target));
        }

        /// <summary>
        /// Pads the data range by the padding fraction on each side; a
        /// zero-range axis expands to ±1 around the value so single points
        /// center instead of collapsing the scale.
        /// </summary>
        private (double Lo, double Hi) PaddedDomain(double min, double max)
        {
            double range = max - min;
            if (range <= 0)
            {
                return (min - 1, max + 1);
            }
            return (min - _padding * range, max + _padding * range);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _points.Clear();
            _width = DefaultWidth;
            _height = DefaultHeight;
            _background = null;
            _pointSize = DefaultPointSize;
            _padding = DefaultPadding;
            _optionsCaptured = false;
        }
    }
}
