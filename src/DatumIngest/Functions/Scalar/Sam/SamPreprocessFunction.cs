using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Sam;

/// <summary>
/// <c>sam_preprocess(img Image) → Struct{tensor: Float32[], scale: Float32, height: Int32, width: Int32}</c>.
/// Performs SAM's input transform: aspect-preserving resize so the longest
/// side equals 1024, then packs the resulting RGB pixels as a flat HWC
/// Float32 buffer in raw [0, 255] range — exactly what the samexporter
/// MobileSAM / SAM encoder graph expects to consume before its internal
/// pad-to-1024×1024 step.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a builtin.</strong> Three things have to happen in lockstep:
/// the longest-side resize, the HWC byte→Float32 pack, and the surfacing of
/// the scale factor + resized dims back into the SQL body (the encoder's
/// input shape is <c>[H, W, 3]</c> where H, W are the resized dims, and the
/// scale is needed to map original-image prompt coordinates into 1024-space).
/// Doing this purely in SQL would need a longest-side resize function plus
/// careful arithmetic and three return values; one builtin returns the whole
/// bundle.
/// </para>
/// <para>
/// <strong>Output struct fields.</strong>
/// <list type="bullet">
///   <item><c>tensor</c> — Float32[ resized_h * resized_w * 3 ] in HWC order,
///   raw byte values in [0, 255].</item>
///   <item><c>scale</c> — Float32 multiplier mapping original-image
///   coordinates into the encoder's 1024-space (<c>scale = 1024 /
///   max(orig_w, orig_h)</c>).</item>
///   <item><c>height</c>, <c>width</c> — Int32 resized dims; the encoder's
///   input tensor shape is <c>[height, width, 3]</c>.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SamPreprocessFunction : IFunction, IScalarFunction
{
    private const float SamLongSideTarget = 1024f;

    /// <inheritdoc />
    public static string Name => "sam_preprocess";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Aspect-preserving resize to longest-side=1024 + HWC raw-byte Float32 pack for SAM-family encoders. " +
        "Returns Struct{tensor Float32[], scale Float32, height Int32, width Int32}. " +
        "Feed `tensor` into infer('encoder', tensor, [height, width, 3]); use `scale` to map " +
        "original-image prompt coords into the encoder's 1024-space.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("img", DataKindMatcher.Exact(DataKind.Image), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SamPreprocessFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullStruct(0));
        }

        SKBitmap source = args[0].AsImage();
        int origW = source.Width;
        int origH = source.Height;
        if (origW <= 0 || origH <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"input image must have positive dimensions; got {origW}×{origH}.");
        }

        float scale = SamLongSideTarget / System.Math.Max(origW, origH);
        int resizedW = System.Math.Max(1, (int)MathF.Round(origW * scale));
        int resizedH = System.Math.Max(1, (int)MathF.Round(origH * scale));

        SKImageInfo info = new(resizedW, resizedH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap resized = source.Resize(info, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"sam_preprocess: SkiaSharp failed to resize {origW}×{origH} → {resizedW}×{resizedH}.");

        int planeSize = resizedH * resizedW;
        float[] hwc = new float[planeSize * 3];
        nint pixelPtr = resized.GetPixels();
        unsafe
        {
            byte* src = (byte*)pixelPtr;
            for (int yx = 0; yx < planeSize; yx++)
            {
                int sb = yx * 4;
                int db = yx * 3;
                hwc[db + 0] = src[sb + 0]; // R
                hwc[db + 1] = src[sb + 1]; // G
                hwc[db + 2] = src[sb + 2]; // B
            }
        }

        ValueRef[] fields =
        [
            ValueRef.FromPrimitiveArray(hwc, DataKind.Float32),
            ValueRef.FromFloat32(scale),
            ValueRef.FromInt32(resizedH),
            ValueRef.FromInt32(resizedW),
        ];

        ushort typeId = 0;
        if (frame.Types is { } types)
        {
            int float32ArrayTypeId = types.InternArrayType(DataKind.Float32);
            int float32ScalarTypeId = types.InternScalarType(DataKind.Float32);
            int int32ScalarTypeId = types.InternScalarType(DataKind.Int32);
            StructFieldDescriptor[] descriptors =
            [
                new("tensor", float32ArrayTypeId),
                new("scale",  float32ScalarTypeId),
                new("height", int32ScalarTypeId),
                new("width",  int32ScalarTypeId),
            ];
            typeId = (ushort)types.InternStructType(descriptors);
        }

        return new ValueTask<ValueRef>(ValueRef.FromStruct(fields, typeId));
    }
}
