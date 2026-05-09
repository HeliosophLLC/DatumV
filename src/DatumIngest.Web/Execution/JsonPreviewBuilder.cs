using System.Formats.Cbor;
using System.Text;
using System.Text.Json;
using DatumIngest.Functions.Json;

namespace DatumIngest.Web.Execution;

/// <summary>
/// Builds a bounded JSON-text preview of a <c>DataKind.Json</c> value's CBOR payload.
/// Large containers (arrays, objects) are truncated to the first
/// <see cref="MaxElements"/> entries or stopped at the <see cref="MaxBytes"/> byte
/// budget — whichever fires first. Scalars and small containers decode in full and
/// carry no truncation metadata.
/// </summary>
/// <remarks>
/// <para>
/// The output text is always valid JSON of the truncated portion so the front-end's
/// existing <c>JSON.parse(cell.text)</c> path keeps working unchanged. Truncation
/// metadata travels alongside as a <see cref="JsonPreviewInfo"/> envelope; consumers
/// that don't read it still get a sensible (smaller) tree to render.
/// </para>
/// <para>
/// Per-element byte capping is best-effort: the cap is checked between elements, so
/// a single oversized first element may exceed the budget. That's by design — losing
/// the only element of a list to "too big" gives users nothing to click on.
/// </para>
/// </remarks>
internal static class JsonPreviewBuilder
{
    /// <summary>
    /// Default per-cell preview cap on array/object element count. Matches
    /// <see cref="DatumIngest.Ingestion.Sampling.SamplePreviewCollector"/>'s
    /// <c>MaxArrayPreviewElements</c> so a column rendered both as a sample-card
    /// chip and as a query-result cell shows the same number of entries.
    /// </summary>
    public const int MaxElements = 16;

    /// <summary>
    /// Default per-cell hard byte ceiling for the preview JSON text. Eight KiB fits
    /// inline in a result-row payload without straining the wire, and large enough
    /// for 16 mid-sized object elements with their field names.
    /// </summary>
    public const int MaxBytes = 8 * 1024;

    /// <summary>
    /// Builds a JSON-text preview plus optional truncation metadata for the given
    /// CBOR-encoded <c>DataKind.Json</c> value. Returns <c>(text, null)</c> when the
    /// value decodes in full under the caps; <c>(partialText, info)</c> when
    /// truncation fired.
    /// </summary>
    public static (string Text, JsonPreviewInfo? Preview) Build(
        ReadOnlySpan<byte> cbor,
        int maxElements = MaxElements,
        int maxBytes = MaxBytes)
    {
        CborReader reader = new(cbor.ToArray(), CborConformanceMode.Canonical);
        CborReaderState rootState = reader.PeekState();

        switch (rootState)
        {
            case CborReaderState.StartArray:
                return BuildArrayPreview(reader, maxElements, maxBytes);

            case CborReaderState.StartMap:
                return BuildMapPreview(reader, maxElements, maxBytes);

            default:
                // Scalars decode in full — they can't exceed any meaningful budget.
                return (CborJsonCodec.DecodeToJsonText(cbor), null);
        }
    }

    private static (string Text, JsonPreviewInfo? Preview) BuildArrayPreview(
        CborReader reader, int maxElements, int maxBytes)
    {
        int? declared = reader.ReadStartArray();
        // Canonical CBOR always carries definite lengths; treat unknown as a very
        // large number so the byte cap drives termination.
        int total = declared ?? int.MaxValue;

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();

            int shown = 0;
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                if (shown >= maxElements || WrittenBytes(writer) >= maxBytes)
                {
                    SkipRemaining(reader, ref total, declared.HasValue);
                    break;
                }

                CopyCborValue(reader, writer);
                shown++;
            }

            if (reader.PeekState() == CborReaderState.EndArray)
            {
                reader.ReadEndArray();
                if (!declared.HasValue) total = shown;
            }

            writer.WriteEndArray();
            writer.Flush();

            string text = Encoding.UTF8.GetString(stream.ToArray());
            JsonPreviewInfo? info = shown < total
                ? new JsonPreviewInfo(total, shown, "array")
                : null;
            return (text, info);
        }
    }

    /// <summary>
    /// Bytes the writer has produced so far, counting both already-flushed bytes
    /// and pending bytes still in the internal buffer. Using <c>Stream.Length</c>
    /// alone misses the buffer and lets the byte cap silently never trip when
    /// elements are small enough to fit in one write batch.
    /// </summary>
    private static long WrittenBytes(Utf8JsonWriter writer) =>
        writer.BytesCommitted + writer.BytesPending;

    private static (string Text, JsonPreviewInfo? Preview) BuildMapPreview(
        CborReader reader, int maxElements, int maxBytes)
    {
        int? declared = reader.ReadStartMap();
        int total = declared ?? int.MaxValue;

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            int shown = 0;
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                if (shown >= maxElements || WrittenBytes(writer) >= maxBytes)
                {
                    SkipRemainingMap(reader, ref total, declared.HasValue);
                    break;
                }

                string key = reader.ReadTextString();
                writer.WritePropertyName(key);
                CopyCborValue(reader, writer);
                shown++;
            }

            if (reader.PeekState() == CborReaderState.EndMap)
            {
                reader.ReadEndMap();
                if (!declared.HasValue) total = shown;
            }

            writer.WriteEndObject();
            writer.Flush();

            string text = Encoding.UTF8.GetString(stream.ToArray());
            JsonPreviewInfo? info = shown < total
                ? new JsonPreviewInfo(total, shown, "object")
                : null;
            return (text, info);
        }
    }

    private static void SkipRemaining(CborReader reader, ref int total, bool hadDeclaredLength)
    {
        if (hadDeclaredLength)
        {
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                reader.SkipValue();
            }
            reader.ReadEndArray();
        }
        else
        {
            int skipped = 0;
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                reader.SkipValue();
                skipped++;
            }
            reader.ReadEndArray();
            total = skipped;
        }
    }

    private static void SkipRemainingMap(CborReader reader, ref int total, bool hadDeclaredLength)
    {
        if (hadDeclaredLength)
        {
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                reader.SkipValue(); // key
                reader.SkipValue(); // value
            }
            reader.ReadEndMap();
        }
        else
        {
            int skipped = 0;
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                reader.SkipValue(); // key
                reader.SkipValue(); // value
                skipped++;
            }
            reader.ReadEndMap();
            total = skipped;
        }
    }

    /// <summary>
    /// Reads one full CBOR value (scalar or container) from <paramref name="reader"/>
    /// and writes its JSON equivalent into <paramref name="writer"/>. Mirrors
    /// <see cref="CborJsonCodec"/>'s decoder so element-level recursion stays canonical.
    /// </summary>
    private static void CopyCborValue(CborReader reader, Utf8JsonWriter writer)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.UnsignedInteger:
                writer.WriteNumberValue(reader.ReadUInt64());
                break;

            case CborReaderState.NegativeInteger:
                writer.WriteNumberValue(reader.ReadInt64());
                break;

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                writer.WriteNumberValue(reader.ReadDouble());
                break;

            case CborReaderState.TextString:
                writer.WriteStringValue(reader.ReadTextString());
                break;

            case CborReaderState.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;

            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;

            case CborReaderState.StartArray:
                reader.ReadStartArray();
                writer.WriteStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    CopyCborValue(reader, writer);
                }
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;

            case CborReaderState.StartMap:
                reader.ReadStartMap();
                writer.WriteStartObject();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    writer.WritePropertyName(reader.ReadTextString());
                    CopyCborValue(reader, writer);
                }
                reader.ReadEndMap();
                writer.WriteEndObject();
                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported CBOR state when copying preview value: {reader.PeekState()}.");
        }
    }
}

/// <summary>
/// Truncation metadata for a <see cref="JsonCell"/> whose <c>Text</c> holds a
/// partial preview of a larger array or object. Sent to the front-end so the
/// grid can render a "N of M shown" chip and the modal can render a banner
/// without having to count the parsed tree itself.
/// </summary>
/// <param name="Total">Total elements / fields in the original CBOR value.</param>
/// <param name="Shown">Number actually emitted in <see cref="JsonCell.Text"/>.</param>
/// <param name="Mode">Either <c>"array"</c> or <c>"object"</c>.</param>
internal sealed record JsonPreviewInfo(int Total, int Shown, string Mode);
