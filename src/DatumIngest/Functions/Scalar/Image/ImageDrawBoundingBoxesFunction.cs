using DatumIngest.Execution;
using DatumIngest.Functions.Image;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Overlays bounding-box rectangles (with optional labels) on an image.
/// <c>image_draw_bounding_boxes(image, boxes)</c> — <c>boxes</c> is an
/// <c>Array&lt;Struct&gt;</c> whose element struct exposes <c>x</c>, <c>y</c>, <c>w</c>,
/// <c>h</c> (numeric, in source-image pixel coordinates, top-left origin) plus
/// optional <c>label</c> (String) and <c>score</c> (numeric, 0–1) fields.
/// Field names are resolved via the per-query <see cref="TypeRegistry"/>; any
/// box-producing model that exposes its <c>OutputFields</c> drops in directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why field-name resolution.</strong> The function refuses to assume a
/// fixed field order — different detectors emit different struct shapes (YOLO is
/// label/score/x/y/w/h, SCRFD is score/x/y/w/h/landmarks, etc.). Using the
/// registry forces every detector to declare its schema (<c>OutputFields</c>),
/// which is the same forcing-function pattern as <c>typeof()</c> on an
/// unregistered struct.
/// </para>
/// <para>
/// <strong>Output.</strong> A new PNG-encoded <see cref="DataKind.Image"/>. The
/// source bitmap is not modified; we draw onto a copy and re-encode.
/// </para>
/// </remarks>
public sealed class ImageDrawBoundingBoxesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_draw_bounding_boxes";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Overlays bounding-box rectangles (with optional labels) on an image. "
        + "The boxes argument is an Array<Struct> with x, y, w, h fields plus "
        + "optional label and score; field names are resolved via the per-query type registry.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("boxes", DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageDrawBoundingBoxesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        ValueRef boxesArg = args[1];

        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        if (!boxesArg.IsArray)
        {
            throw new FunctionArgumentException(Name,
                $"second argument must be an Array<Struct>; got Kind={boxesArg.Kind}, IsArray=false.");
        }

        SKBitmap source = imgArg.AsImage();

        // Null array or empty array — pass the source through unchanged. We
        // still re-encode so the result is owned by this function (consumers
        // may mutate the SKBitmap downstream).
        if (boxesArg.IsNull)
        {
            return new ValueTask<ValueRef>(EncodePassThrough(source));
        }

        ReadOnlySpan<ValueRef> elements = boxesArg.GetArrayElements();
        if (elements.Length == 0)
        {
            return new ValueTask<ValueRef>(EncodePassThrough(source));
        }

        // Resolve field indices from the first non-null element's TypeId. Per
        // the per-element TypeId model, each slot carries its own shape; the
        // array container itself doesn't. Falling back across elements lets
        // us tolerate a leading null without a per-row registry hop.
        FieldIndices indices = ResolveFieldIndices(elements, frame.Types);

        return new ValueTask<ValueRef>(DrawBoxes(source, elements, indices));
    }

    /// <summary>
    /// Encodes <paramref name="source"/> as PNG without drawing anything. Used
    /// for the null/empty-boxes pass-through so the result is always a fresh
    /// PNG owned by this function.
    /// </summary>
    private static ValueRef EncodePassThrough(SKBitmap source)
    {
        byte[] png = ImageEncoder.Encode(source, SKEncodedImageFormat.Png, 100);
        return ValueRef.FromBytes(DataKind.Image, png);
    }

    private static ValueRef DrawBoxes(
        SKBitmap source, ReadOnlySpan<ValueRef> elements, FieldIndices indices)
    {
        using SKBitmap copy = source.Copy()
            ?? throw new InvalidOperationException(
                "image_draw_bounding_boxes: SKBitmap.Copy returned null — out of memory or unsupported color type.");
        using SKCanvas canvas = new(copy);

        using SKPaint stroke = new()
        {
            Color = new SKColor(0xFF, 0x40, 0x40),  // red
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(2f, source.Width / 400f),
            IsAntialias = true,
        };

        using SKPaint labelBg = new()
        {
            Color = new SKColor(0xFF, 0x40, 0x40, 0xCC),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        using SKPaint labelText = new()
        {
            Color = SKColors.White,
            IsAntialias = true,
        };

        using SKFont font = new(SKTypeface.Default, MathF.Max(10f, source.Height / 40f));

        for (int i = 0; i < elements.Length; i++)
        {
            ValueRef element = elements[i];
            if (element.IsNull) continue;

            ReadOnlySpan<ValueRef> fields = element.GetStructFields();

            // Resolve x/y/w/h either from a nested `bbox: BoundingBox` field
            // (named-type detection shapes — ScoredDetection, FaceDetection,
            // OcrLine, RegionScore, etc.) or from flat x/y/w/h fields
            // (legacy detection shapes pre-named-types).
            float x, y, w, h;
            if (indices.BBox >= 0)
            {
                ReadOnlySpan<ValueRef> bboxFields = fields[indices.BBox].GetStructFields();
                x = bboxFields[indices.X].ToFloat();
                y = bboxFields[indices.Y].ToFloat();
                w = bboxFields[indices.W].ToFloat();
                h = bboxFields[indices.H].ToFloat();
            }
            else
            {
                x = fields[indices.X].ToFloat();
                y = fields[indices.Y].ToFloat();
                w = fields[indices.W].ToFloat();
                h = fields[indices.H].ToFloat();
            }

            canvas.DrawRect(x, y, w, h, stroke);

            string? labelText_ = BuildLabelText(fields, indices);
            if (labelText_ is null) continue;

            // Label background: tight rectangle above the box (or inside the
            // top edge when y is near 0). Pad the text bounds a couple of
            // pixels for readability.
            font.MeasureText(labelText_, out SKRect textBounds, labelText);
            float padding = 3f;
            float textWidth = textBounds.Width + padding * 2;
            float textHeight = font.Size + padding * 2;
            float labelX = x;
            float labelY = y >= textHeight ? y - textHeight : y;

            canvas.DrawRect(labelX, labelY, textWidth, textHeight, labelBg);
            canvas.DrawText(
                labelText_,
                labelX + padding,
                labelY + padding + font.Size - font.Metrics.Descent,
                SKTextAlign.Left,
                font,
                labelText);
        }

        byte[] png = ImageEncoder.Encode(copy, SKEncodedImageFormat.Png, 100);
        return ValueRef.FromBytes(DataKind.Image, png);
    }

    /// <summary>
    /// Builds the text shown next to a box — <c>"label 0.94"</c> when both
    /// fields are present, <c>"label"</c> when only label is, <c>"0.94"</c>
    /// when only score is, <see langword="null"/> when neither is provided
    /// (skip drawing the label background entirely).
    /// </summary>
    private static string? BuildLabelText(ReadOnlySpan<ValueRef> fields, FieldIndices indices)
    {
        string? label = null;
        if (indices.Label >= 0)
        {
            ValueRef f = fields[indices.Label];
            if (!f.IsNull) label = f.AsString();
        }

        string? score = null;
        if (indices.Score >= 0)
        {
            ValueRef f = fields[indices.Score];
            if (!f.IsNull) score = f.ToFloat().ToString("0.00");
        }

        return (label, score) switch
        {
            (not null, not null) => $"{label} {score}",
            (not null, null) => label,
            (null, not null) => score,
            _ => null,
        };
    }

    private static FieldIndices ResolveFieldIndices(
        ReadOnlySpan<ValueRef> elements, TypeRegistry? registry)
    {
        if (registry is null)
        {
            throw new FunctionArgumentException(Name,
                "no TypeRegistry on the evaluation frame. The boxes argument's struct shape "
                + "must be registered with the per-query TypeRegistry so x/y/w/h fields "
                + "can be resolved by name.");
        }

        ushort typeId = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].IsNull)
            {
                typeId = elements[i].TypeId;
                break;
            }
        }
        if (typeId == 0)
        {
            throw new FunctionArgumentException(Name,
                "boxes argument's element struct has no TypeId. Every box-producing model "
                + "must declare its OutputFields so the engine can intern the shape and "
                + "stamp a TypeId on each row.");
        }

        TypeDescriptor? desc = registry.GetDescriptor(typeId);
        if (desc?.Fields is not { } fields)
        {
            throw new FunctionArgumentException(Name,
                $"boxes argument's TypeId {typeId} is not registered as a struct shape.");
        }

        // Two acceptable shapes:
        //   1. Nested bbox: element has a `bbox` field of struct type whose
        //      own fields include x/y/w/h. The named-type vocabulary
        //      (BoundingBox, ScoredDetection, FaceDetection, etc.) uses
        //      this shape — bbox is composable as a sub-structure.
        //   2. Flat: element has x/y/w/h as direct fields (legacy detector
        //      shapes pre-named-types).
        int bboxIndex = FindField(fields, "bbox");
        int x, y, w, h;
        if (bboxIndex >= 0)
        {
            // Nested-bbox shape. Resolve the inner BoundingBox descriptor
            // and lift its x/y/w/h indices.
            int bboxTypeId = fields[bboxIndex].TypeId;
            TypeDescriptor? bboxDesc = registry.GetDescriptor(bboxTypeId);
            if (bboxDesc?.Fields is not { } bboxFields)
            {
                throw new FunctionArgumentException(Name,
                    $"boxes argument's `bbox` field (TypeId {bboxTypeId}) is not a registered struct.");
            }
            x = FindField(bboxFields, "x");
            y = FindField(bboxFields, "y");
            w = FindField(bboxFields, "w");
            h = FindField(bboxFields, "h");
            if (x < 0 || y < 0 || w < 0 || h < 0)
            {
                string have = string.Join(", ", bboxFields.Select(f => f.Name));
                throw new FunctionArgumentException(Name,
                    $"boxes argument's `bbox` struct must have x, y, w, h fields; got [{have}]. "
                    + "Use BoundingBox (or a struct with the same x/y/w/h fields) as the bbox type.");
            }
        }
        else
        {
            // Flat shape — backward-compat path.
            x = FindField(fields, "x");
            y = FindField(fields, "y");
            w = FindField(fields, "w");
            h = FindField(fields, "h");
            if (x < 0 || y < 0 || w < 0 || h < 0)
            {
                string have = string.Join(", ", fields.Select(f => f.Name));
                throw new FunctionArgumentException(Name,
                    $"boxes argument's struct must have either a nested `bbox: BoundingBox` field "
                    + $"OR flat x, y, w, h fields; got [{have}].");
            }
        }

        return new FieldIndices(
            BBox: bboxIndex,
            X: x, Y: y, W: w, H: h,
            Label: FindField(fields, "label"),
            Score: FindField(fields, "score"));
    }

    private static int FindField(IReadOnlyList<StructFieldDescriptor> fields, string name)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Field indices into the boxes' element struct.
    /// <para>
    /// <paramref name="BBox"/> is -1 for flat shapes (legacy detector
    /// outputs with x/y/w/h on the element struct) and ≥0 for the
    /// nested-bbox shape used by every named-type detection contract
    /// (<see cref="NamedTypeRegistry"/>'s <c>BoundingBox</c> nested into
    /// <c>ScoredDetection</c> / <c>FaceDetection</c> / <c>OcrLine</c> /
    /// <c>RegionScore</c>). When BBox ≥ 0, <paramref name="X"/> /
    /// <paramref name="Y"/> / <paramref name="W"/> / <paramref name="H"/>
    /// index into the *inner* BoundingBox's fields; otherwise into the
    /// outer element struct directly.
    /// </para>
    /// <para>
    /// <paramref name="Label"/> and <paramref name="Score"/> are -1 when
    /// the optional field is absent.
    /// </para>
    /// </summary>
    private readonly record struct FieldIndices(int BBox, int X, int Y, int W, int H, int Label, int Score);
}
