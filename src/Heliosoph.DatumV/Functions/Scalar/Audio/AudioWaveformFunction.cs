using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Audio;
using Heliosoph.DatumV.Functions.Scalar.Drawing;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_waveform(audio Audio, width Int32, height Int32, options Struct) → Image</c>.
/// One-liner sugar over the envelope + drawing layers: decodes the audio,
/// builds a per-pixel-column peak envelope, and renders the classic
/// Audacity-style vertical-bars waveform onto a solid background. Use this
/// when you just want the picture; reach for <c>audio_waveform_envelope</c>
/// + <c>audio_waveform_drawing</c> when you want stylisation control.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Options struct fields.</strong>
/// <list type="bullet">
///   <item><c>fg: Color</c> — waveform stroke colour.</item>
///   <item><c>bg: Color</c> — background fill colour.</item>
/// </list>
/// Construct via the inline struct literal:
/// <c>{fg: color_hex('#7fdbff'), bg: color_hex('#111')}</c>. Field order
/// doesn't matter; the function resolves names via the runtime type
/// descriptor.
/// </para>
/// <para>
/// <strong>Style.</strong> Bin count equals <c>width</c> — one envelope
/// bin per output pixel column — and each bin is rendered as a 1-pixel-wide
/// vertical line from the bin minimum to the bin maximum. Amplitudes map
/// to <c>y = height/2 - amplitude * height/2</c>; the centreline sits at
/// <c>height/2</c>. For richer styles (gradients, dots, mirrored,
/// filled-under-curve, smoothed) go through <c>audio_waveform_drawing</c>
/// or <c>audio_waveform_path</c> directly.
/// </para>
/// </remarks>
public sealed class AudioWaveformFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "audio_waveform";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Renders an audio waveform as a peak-envelope bars Image at width × height. "
        + "The options struct carries {fg: Color, bg: Color}. Sugar over "
        + "audio_waveform_envelope + per-column line composition — reach for the "
        + "lower-level functions when you need gradients, smoothing, custom styles.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("audio",   DataKindMatcher.Exact(DataKind.Audio)),
                new ParameterSpec("width",   DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Output image width; doubles as the envelope bin count.")),
                new ParameterSpec("height",  DataKindMatcher.Family(DataKindFamily.IntegerFamily),
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Output image height; centreline sits at height/2.")),
                new ParameterSpec("options", DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioWaveformFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef audioArg = args[0];
        ValueRef widthArg = args[1];
        ValueRef heightArg = args[2];
        ValueRef optionsArg = args[3];

        if (audioArg.IsNull || widthArg.IsNull || heightArg.IsNull || optionsArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int width = widthArg.ToInt32();
        int height = heightArg.ToInt32();
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"width and height must be > 0; got width={width}, height={height}.");
        }

        (SKColor fg, SKColor bg) = ReadOptionsStruct(optionsArg, frame);

        DataValue audioValue = audioArg.ToDataValue(frame.Source);
        byte[] audioBytes = audioValue.AsAudio(frame.Source, frame.SidecarRegistry);
        float[] samples;
        try
        {
            samples = AudioPcmDecoder.DecodeDownmixedFloat32(audioBytes, out _);
        }
        catch (InvalidOperationException ex)
        {
            throw new FunctionArgumentException(Name, ex.Message);
        }

        SKBitmap bitmap = Render(samples, width, height, fg, bg);
        return new ValueTask<ValueRef>(ValueRef.FromImage(bitmap));
    }

    /// <summary>
    /// Reads the <c>options</c> struct's <c>fg</c> and <c>bg</c> Color fields
    /// by name via the runtime type descriptor. Mirrors the field-lookup
    /// pattern used by <c>image_crop</c>'s rect struct.
    /// </summary>
    private static (SKColor Fg, SKColor Bg) ReadOptionsStruct(ValueRef options, EvaluationFrame frame)
    {
        TypeDescriptor? desc = frame.Types?.GetDescriptor(options.TypeId);
        if (desc?.Fields is null)
        {
            throw new FunctionArgumentException(Name,
                "options struct has no registered field descriptors; expected fields fg, bg.");
        }
        int fgIdx = desc.FindFieldIndex("fg");
        int bgIdx = desc.FindFieldIndex("bg");
        if (fgIdx < 0 || bgIdx < 0)
        {
            throw new FunctionArgumentException(Name,
                $"options struct must have fields fg, bg (both Color); got "
                + $"[{string.Join(", ", desc.Fields.Select(f => f.Name))}].");
        }
        ReadOnlySpan<ValueRef> fields = options.GetStructFields();
        ValueRef fgField = fields[fgIdx];
        ValueRef bgField = fields[bgIdx];
        if (fgField.Kind != DataKind.Color || bgField.Kind != DataKind.Color)
        {
            throw new FunctionArgumentException(Name,
                $"options.fg and options.bg must both be Color; got "
                + $"fg={fgField.Kind}, bg={bgField.Kind}.");
        }
        return (DrawingHelpers.ToSKColor(fgField), DrawingHelpers.ToSKColor(bgField));
    }

    /// <summary>
    /// Bins <paramref name="samples"/> into <paramref name="width"/> columns,
    /// rasterises the per-column peak envelope as vertical 1-pixel lines in
    /// <paramref name="fg"/> over a solid <paramref name="bg"/> background.
    /// </summary>
    private static SKBitmap Render(
        ReadOnlySpan<float> samples, int width, int height, SKColor fg, SKColor bg)
    {
        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(bg);

        if (samples.Length == 0)
        {
            // No audio to render — return the solid-background canvas.
            return bitmap;
        }

        using SKPaint paint = new()
        {
            Color = fg,
            StrokeWidth = 1f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = false,
        };

        float midY = height * 0.5f;
        double samplesPerBin = (double)samples.Length / width;
        for (int b = 0; b < width; b++)
        {
            int start = (int)System.Math.Floor(b * samplesPerBin);
            int end = (int)System.Math.Floor((b + 1) * samplesPerBin);
            if (end > samples.Length) end = samples.Length;
            if (start >= end)
            {
                continue;
            }
            float lo = samples[start];
            float hi = lo;
            for (int i = start + 1; i < end; i++)
            {
                float v = samples[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            float yMax = midY - hi * midY;
            float yMin = midY - lo * midY;
            // Skia's stroked vertical line at integer x lands on the pixel column.
            // +0.5 keeps the 1-pixel stroke centred on the column rather than
            // straddling two columns.
            float x = b + 0.5f;
            canvas.DrawLine(x, yMax, x, yMin, paint);
        }

        return bitmap;
    }
}
