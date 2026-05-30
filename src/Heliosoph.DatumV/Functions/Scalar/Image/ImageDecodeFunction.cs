using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_decode(bytes Array&lt;UInt8&gt;) → Image</c> /
/// <c>image_decode(path String) → Image</c>. Wraps raw encoded image bytes
/// (PNG / JPEG / WebP / BMP / TIFF, anything SkiaSharp recognises) as a typed
/// <c>Image</c> value so subsequent image functions (<c>image_width</c>,
/// <c>image_height</c>, <c>image_crop</c>, …) can consume it directly. The
/// proximate use case is SQL recipes that compose
/// <c>image_decode(open_archive(:source, …).bytes)</c> to lift a JPEG / PNG
/// entry pulled out of an archive into the engine's typed-image surface
/// without ever touching disk. The path overload is the matching convenience
/// for one-off lookups against a file on the local filesystem.
/// </summary>
/// <remarks>
/// Mirror of <c>audio_decode</c>: no PNG/JPEG pixel decoding happens — bytes
/// pass through verbatim with the kind tag flipped to
/// <see cref="DataKind.Image"/>. Width/height parsing happens lazily at the
/// materialization boundary the same way it does for ingest-time
/// <see cref="DataValue.FromImage(byte[], IValueStore)"/> calls, so
/// <c>image_width()</c> and friends keep their fast inline-metadata path.
/// </remarks>
public sealed class ImageDecodeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_decode";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Wraps a raw encoded-image byte array (or a filesystem path) as a typed "
        + "Image value: image_decode(bytes Array<UInt8>) → Image / "
        + "image_decode(path String) → Image. No pixel decoding — bytes pass "
        + "through verbatim. Composes with open_archive / open_folder: "
        + "image_decode(o.bytes) lifts a JPEG/PNG/WebP archive entry into the engine's "
        + "typed-image surface for downstream image_* accessors and model invocations.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8),
                    IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String),
                    IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageDecodeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        if (arg.Kind == DataKind.String)
        {
            return ReadFromPathAsync(arg.AsString(), cancellationToken);
        }

        byte[] bytes = arg.AsBytes();
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.Image, bytes));
    }

    private static async ValueTask<ValueRef> ReadFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return ValueRef.FromBytes(DataKind.Image, bytes);
    }
}
