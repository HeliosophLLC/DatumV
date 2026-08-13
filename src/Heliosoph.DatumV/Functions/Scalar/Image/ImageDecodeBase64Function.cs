using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>image_decode_base64(text String) → Image</c>. The base64 sibling of
/// <see cref="ImageDecodeFunction"/>: takes a base64-encoded string, decodes it
/// to the raw encoded image bytes (PNG / JPEG / WebP / BMP / TIFF, anything
/// SkiaSharp recognises), and wraps the result as a typed <c>Image</c> value so
/// subsequent image functions (<c>image_width</c>, <c>image_height</c>,
/// <c>image_crop</c>, …) can consume it directly. The proximate use case is
/// lifting an image that arrives already base64-encoded — a JSON payload field,
/// a data URI, an API response — into the engine's typed-image surface without
/// a separate <c>decode(text, 'base64')</c> hop.
/// </summary>
/// <remarks>
/// <para>
/// A leading data-URI header (<c>data:image/png;base64,</c>) is stripped
/// automatically when present, so both a bare base64 payload and a full
/// <c>data:</c> URI decode the same way.
/// </para>
/// <para>
/// Base64 decoding is strict: syntactically invalid base64 throws
/// <see cref="FunctionArgumentException"/>, mirroring <c>decode(text,
/// 'base64')</c>. Once decoded, image-format handling is permissive exactly like
/// <see cref="ImageDecodeFunction"/> — no pixel decoding happens, the bytes pass
/// through verbatim with the kind tag flipped to <see cref="DataKind.Image"/>,
/// and unrecognised / corrupt image bytes still produce a non-NULL
/// <see cref="DataKind.Image"/> value with zero-sentinel inline metadata.
/// Width/height parsing happens lazily at the materialization boundary, so
/// <c>image_width()</c> and friends keep their fast inline-metadata path.
/// Callers that want a hard error on non-image bytes — with a hex-byte preview
/// of what didn't match — should use <c>CAST(decode(text, 'base64') AS Image)</c>
/// instead, which validates against the PNG/JPEG/WebP/GIF/BMP/TIFF magic table.
/// </para>
/// </remarks>
public sealed class ImageDecodeBase64Function : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_decode_base64";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Decodes a base64-encoded string into a typed Image value: "
        + "image_decode_base64(text String) → Image. Strips an optional "
        + "data:...;base64, URI prefix, base64-decodes the payload, then wraps "
        + "the raw encoded-image bytes verbatim (no pixel decoding) for downstream "
        + "image_* accessors and model invocations. Base64 sibling of image_decode.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String),
                    IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageDecodeBase64Function>(argumentKinds);

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

        string payload = StripDataUriPrefix(arg.AsString());

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new FunctionArgumentException(
                Name,
                $"input is not valid base64: {ex.Message}");
        }

        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.Image, bytes));
    }

    /// <summary>
    /// Strips a leading <c>data:[&lt;media-type&gt;];base64,</c> URI header when
    /// present, returning just the base64 payload. Input without such a header is
    /// returned unchanged.
    /// </summary>
    private static string StripDataUriPrefix(string text)
    {
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        int comma = text.IndexOf(',');
        return comma >= 0 ? text[(comma + 1)..] : text;
    }
}
