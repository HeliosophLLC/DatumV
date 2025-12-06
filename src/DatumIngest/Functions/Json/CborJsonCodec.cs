using System.Formats.Cbor;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Functions.Json;

/// <summary>
/// JSON ↔ canonical-CBOR codec used by the <c>json_*</c> function family. JSON
/// text is the user-facing wire format; canonical CBOR (RFC 7049 §3.9) is the
/// arena-resident binary representation. Canonical encoding produces a single
/// bit-pattern per logical value, so byte-equality on the encoded bytes
/// implies value-equality — this is what makes <c>=</c>, hashing, GROUP BY, and
/// DISTINCT on <see cref="DataKind.Json"/> values work without decoding.
/// </summary>
/// <remarks>
/// <para>
/// Number policy is conservative: JSON integers that fit in <see cref="long"/>
/// encode as CBOR signed/unsigned integers (smallest-fit); other numbers
/// encode as float64. Numbers exceeding the int64 range and unrepresentable
/// as a finite float throw — better to fail loudly than silently downcast.
/// </para>
/// <para>
/// Path resolution (<see cref="WalkPath"/>) supports a small JSONPath subset
/// sufficient for LLM-output access: <c>$</c> (root), <c>$.field</c>,
/// <c>$.foo.bar</c>, <c>$.arr[N]</c>, and combinations. No <c>[*]</c>, no
/// recursive descent, no filter expressions. Walk cost is O(n) on the source
/// CBOR — adequate for typical LLM documents (tens to hundreds of fields);
/// a JSONB-style offset table would be needed for very large documents.
/// </para>
/// </remarks>
public static class CborJsonCodec
{
    private const CborConformanceMode Mode = CborConformanceMode.Canonical;

    // ─────────────────────── Encode: JSON text → CBOR ───────────────────────

    /// <summary>
    /// Parses <paramref name="jsonText"/> into a canonical CBOR byte array.
    /// </summary>
    /// <exception cref="JsonException">Input is not valid JSON.</exception>
    /// <exception cref="OverflowException">Input contains a number that exceeds int64 range and cannot be represented as a finite float64.</exception>
    public static byte[] EncodeFromJsonText(string jsonText)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        CborWriter writer = new(Mode);
        WriteJsonElement(writer, doc.RootElement);
        return writer.Encode();
    }

    private static void WriteJsonElement(CborWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                writer.WriteNull();
                break;

            case JsonValueKind.True:
                writer.WriteBoolean(true);
                break;

            case JsonValueKind.False:
                writer.WriteBoolean(false);
                break;

            case JsonValueKind.String:
                writer.WriteTextString(element.GetString()!);
                break;

            case JsonValueKind.Number:
                // Conservative number policy: prefer integer encoding when the
                // value fits in int64; fall back to double; throw on anything
                // beyond. Canonical mode picks the smallest CBOR int encoding.
                if (element.TryGetInt64(out long signedValue))
                {
                    writer.WriteInt64(signedValue);
                }
                else if (element.TryGetUInt64(out ulong unsignedValue))
                {
                    writer.WriteUInt64(unsignedValue);
                }
                else if (element.TryGetDouble(out double doubleValue) && double.IsFinite(doubleValue))
                {
                    writer.WriteDouble(doubleValue);
                }
                else
                {
                    throw new OverflowException(
                        $"JSON number '{element.GetRawText()}' cannot be represented as int64 or finite float64.");
                }
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray(element.GetArrayLength());
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteJsonElement(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.Object:
                // EnumerateObject doesn't expose count without iterating; one pass
                // to count, one pass to write. JsonElement caches its tape so this
                // is cheap (no re-parse).
                int memberCount = 0;
                foreach (JsonProperty _ in element.EnumerateObject())
                {
                    memberCount++;
                }
                writer.WriteStartMap(memberCount);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    writer.WriteTextString(prop.Name);
                    WriteJsonElement(writer, prop.Value);
                }
                writer.WriteEndMap();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unhandled JsonValueKind: {element.ValueKind}.");
        }
    }

    // ─────────────────────── Decode: CBOR → JSON text ───────────────────────

    /// <summary>
    /// Decodes canonical CBOR bytes back to JSON text. Used by output paths
    /// (<see cref="DataKind.Json"/> → <see cref="DataKind.String"/> casts,
    /// table-formatter previews, <c>.dump</c> file output).
    /// </summary>
    public static string DecodeToJsonText(ReadOnlySpan<byte> cbor)
    {
        // CborReader requires ReadOnlyMemory<byte>; copy the span once.
        byte[] copy = cbor.ToArray();
        CborReader reader = new(copy, Mode);
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            ReadCborToJson(reader, writer);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static void ReadCborToJson(CborReader reader, Utf8JsonWriter writer)
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
                writer.WriteStartArray();
                reader.ReadStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    ReadCborToJson(reader, writer);
                }
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;

            case CborReaderState.StartMap:
                writer.WriteStartObject();
                reader.ReadStartMap();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    string key = reader.ReadTextString();
                    writer.WritePropertyName(key);
                    ReadCborToJson(reader, writer);
                }
                reader.ReadEndMap();
                writer.WriteEndObject();
                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported CBOR state when decoding to JSON: {reader.PeekState()}.");
        }
    }

    // ─────────────────────── Path walking ───────────────────────

    /// <summary>
    /// Result of a <see cref="WalkPath"/> traversal. Three states: not-found,
    /// scalar (a typed leaf value materialised at the path), or subdocument
    /// (an offset-and-length window into the source CBOR bytes).
    /// </summary>
    public readonly struct CborWalkResult
    {
        /// <summary>Discriminates between not-found, scalar, and subdocument results.</summary>
        public CborWalkResultKind Kind { get; }

        /// <summary>Valid when <see cref="Kind"/> is <see cref="CborWalkResultKind.Scalar"/>.</summary>
        public ValueRef Scalar { get; }

        /// <summary>Valid when <see cref="Kind"/> is <see cref="CborWalkResultKind.Subdocument"/>.</summary>
        public int SubdocOffset { get; }

        /// <summary>Valid when <see cref="Kind"/> is <see cref="CborWalkResultKind.Subdocument"/>.</summary>
        public int SubdocLength { get; }

        private CborWalkResult(CborWalkResultKind kind, ValueRef scalar, int offset, int length)
        {
            Kind = kind;
            Scalar = scalar;
            SubdocOffset = offset;
            SubdocLength = length;
        }

        /// <summary>Constructs the not-found result.</summary>
        public static CborWalkResult NotFound() => new(CborWalkResultKind.NotFound, default, 0, 0);

        /// <summary>Constructs a scalar result wrapping <paramref name="scalar"/>.</summary>
        public static CborWalkResult OfScalar(ValueRef scalar) => new(CborWalkResultKind.Scalar, scalar, 0, 0);

        /// <summary>Constructs a subdocument result describing the byte window <c>[offset, offset + length)</c>.</summary>
        public static CborWalkResult OfSubdocument(int offset, int length) =>
            new(CborWalkResultKind.Subdocument, default, offset, length);
    }

    /// <summary>Discriminator for <see cref="CborWalkResult"/>.</summary>
    public enum CborWalkResultKind
    {
        /// <summary>The path didn't resolve to any value.</summary>
        NotFound,

        /// <summary>The path resolved to a scalar leaf — read <see cref="CborWalkResult.Scalar"/>.</summary>
        Scalar,

        /// <summary>The path resolved to an object/array — read <see cref="CborWalkResult.SubdocOffset"/> and <see cref="CborWalkResult.SubdocLength"/>.</summary>
        Subdocument,
    }

    /// <summary>
    /// Walks <paramref name="cbor"/> following <paramref name="jsonPath"/> and
    /// returns either a typed scalar (for leaf values) or an offset-and-length
    /// window (for object/array subdocuments). Missing keys, out-of-range indices,
    /// and type mismatches (descending into a non-container) all return
    /// <see cref="CborWalkResultKind.NotFound"/> rather than throwing — callers
    /// (<c>json_value</c> / <c>json_query</c>) translate this into SQL NULL.
    /// </summary>
    public static CborWalkResult WalkPath(ReadOnlySpan<byte> cbor, string jsonPath)
    {
        // Path "$" with no steps means "the whole document". Common enough to fast-path.
        IReadOnlyList<PathStep> steps = ParsePath(jsonPath);

        // CborReader needs ReadOnlyMemory; the codec will copy the span once on
        // entry. Subdocument extraction returns offsets relative to the copy,
        // which is byte-equivalent to the source span.
        byte[] cborArray = cbor.ToArray();
        int totalLen = cborArray.Length;
        CborReader reader = new(cborArray, Mode);

        // Descend the path. After each step the reader is positioned at the
        // matched value (just before reading it). NotFound returns short-circuit.
        foreach (PathStep step in steps)
        {
            if (step.IsField)
            {
                if (reader.PeekState() != CborReaderState.StartMap)
                {
                    return CborWalkResult.NotFound();
                }
                int? mapCount = reader.ReadStartMap();
                bool found = false;
                int count = mapCount ?? int.MaxValue;
                for (int i = 0; i < count && reader.PeekState() != CborReaderState.EndMap; i++)
                {
                    if (reader.PeekState() != CborReaderState.TextString)
                    {
                        // Canonical CBOR uses text string keys; a non-text key here
                        // means a non-JSON map made it in somehow. Fail to find rather
                        // than throw — caller treats as missing.
                        return CborWalkResult.NotFound();
                    }
                    string key = reader.ReadTextString();
                    if (key == step.Field)
                    {
                        found = true;
                        // Reader now positioned at the value of our key. Drop out of
                        // the inner loop; outer foreach moves to the next step (or
                        // exits if this was the last step).
                        break;
                    }
                    reader.SkipValue();
                }
                if (!found)
                {
                    return CborWalkResult.NotFound();
                }
                // Note: we don't ReadEndMap — the source map remains "open" but the
                // reader's position is at the value we matched. SkipValue on that
                // value (in subdocument extraction below) skips just our value;
                // any sibling keys after us are unread but that's harmless because
                // we never come back to this map (the next step descends into the
                // matched value).
            }
            else
            {
                if (reader.PeekState() != CborReaderState.StartArray)
                {
                    return CborWalkResult.NotFound();
                }
                int? arrayCount = reader.ReadStartArray();
                int targetIndex = step.Index;
                if (arrayCount.HasValue && (targetIndex < 0 || targetIndex >= arrayCount.Value))
                {
                    return CborWalkResult.NotFound();
                }
                for (int i = 0; i < targetIndex; i++)
                {
                    if (reader.PeekState() == CborReaderState.EndArray)
                    {
                        return CborWalkResult.NotFound();
                    }
                    reader.SkipValue();
                }
                if (reader.PeekState() == CborReaderState.EndArray)
                {
                    return CborWalkResult.NotFound();
                }
            }
        }

        // Reader positioned at the result. Decide scalar vs subdocument.
        int startPos = totalLen - reader.BytesRemaining;
        CborReaderState state = reader.PeekState();

        switch (state)
        {
            case CborReaderState.UnsignedInteger:
                ulong u = reader.ReadUInt64();
                // Conservative widening: prefer Int64 when fits, else UInt64.
                return u <= long.MaxValue
                    ? CborWalkResult.OfScalar(ValueRef.FromInt64((long)u))
                    : CborWalkResult.OfScalar(ValueRef.FromUInt64(u));

            case CborReaderState.NegativeInteger:
                return CborWalkResult.OfScalar(ValueRef.FromInt64(reader.ReadInt64()));

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                return CborWalkResult.OfScalar(ValueRef.FromFloat64(reader.ReadDouble()));

            case CborReaderState.TextString:
                return CborWalkResult.OfScalar(ValueRef.FromString(reader.ReadTextString()));

            case CborReaderState.Boolean:
                return CborWalkResult.OfScalar(ValueRef.FromBoolean(reader.ReadBoolean()));

            case CborReaderState.Null:
                reader.ReadNull();
                return CborWalkResult.OfScalar(ValueRef.Null(DataKind.Unknown));

            case CborReaderState.StartArray:
            case CborReaderState.StartMap:
                // Subdocument: skip past the value; the bytes between startPos
                // and the new position are the subdocument's CBOR encoding.
                reader.SkipValue();
                int endPos = totalLen - reader.BytesRemaining;
                return CborWalkResult.OfSubdocument(startPos, endPos - startPos);

            default:
                throw new InvalidDataException(
                    $"Unexpected CBOR state at resolved path: {state}.");
        }
    }

    // ─────────────────────── Path parser ───────────────────────

    private readonly struct PathStep
    {
        public string? Field { get; }
        public int Index { get; }
        public bool IsField => Field is not null;

        public PathStep(string field) { Field = field; Index = -1; }
        public PathStep(int index) { Field = null; Index = index; }
    }

    /// <summary>
    /// Parses the supported JSONPath subset: <c>$</c>, <c>$.field</c>, <c>$.foo.bar</c>,
    /// <c>$.arr[N]</c>, and combinations. Returns the empty list for the bare <c>$</c>
    /// root.
    /// </summary>
    private static IReadOnlyList<PathStep> ParsePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("JSON path must be non-empty (use \"$\" for the root).", nameof(path));
        }
        if (path[0] != '$')
        {
            throw new ArgumentException(
                $"JSON path must start with '$'; got '{path}'.", nameof(path));
        }

        if (path.Length == 1)
        {
            return Array.Empty<PathStep>();
        }

        List<PathStep> steps = new();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }
                if (i == start)
                {
                    throw new ArgumentException(
                        $"Empty field name at position {start} in JSON path '{path}'.", nameof(path));
                }
                steps.Add(new PathStep(path[start..i]));
            }
            else if (path[i] == '[')
            {
                i++;
                int start = i;
                while (i < path.Length && path[i] != ']')
                {
                    i++;
                }
                if (i >= path.Length)
                {
                    throw new ArgumentException(
                        $"Unclosed array bracket starting at position {start - 1} in JSON path '{path}'.", nameof(path));
                }
                if (!int.TryParse(path[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    throw new ArgumentException(
                        $"Invalid array index '{path[start..i]}' in JSON path '{path}'.", nameof(path));
                }
                if (idx < 0)
                {
                    throw new ArgumentException(
                        $"Negative array index {idx} not supported in JSON path '{path}'.", nameof(path));
                }
                steps.Add(new PathStep(idx));
                i++; // past ']'
            }
            else
            {
                throw new ArgumentException(
                    $"Unexpected character '{path[i]}' at position {i} in JSON path '{path}'.", nameof(path));
            }
        }

        return steps;
    }
}
