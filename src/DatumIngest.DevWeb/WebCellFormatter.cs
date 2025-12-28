using System.Globalization;
using System.Text.Json;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions.Json;
using DatumIngest.Model;

namespace DatumIngest.DevWeb;

internal static class WebCellFormatter
{
    public static JsonCell Format(DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types = null)
    {
        if (value.IsNull)
        {
            return new JsonCell("null");
        }

        // Image / Audio / Video → base64 data so the browser can render them.
        // The legacy byte-array column path (UInt8 + IsArray) carries image
        // payloads from older datasets; treat it as image media too.
        if (value.Kind == DataKind.Image || value.IsByteArrayKind)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
            string mime = DetectImageMime(bytes);
            return new JsonCell("media", Mime: mime, DataB64: Convert.ToBase64String(bytes));
        }

        if (value.Kind == DataKind.Audio)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
            string mime = DetectAudioMime(bytes);
            return new JsonCell("media", Mime: mime, DataB64: Convert.ToBase64String(bytes));
        }

        if (value.Kind == DataKind.Video)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
            string mime = DetectVideoMime(bytes);
            return new JsonCell("media", Mime: mime, DataB64: Convert.ToBase64String(bytes));
        }

        // Json values arrive as canonical CBOR bytes. Decode to JSON text here
        // so the browser can pretty-print and style them; falling through to
        // FormatText would just serialize the raw bytes via DataValue.ToString().
        // On decode failure we degrade to a text cell with the byte count —
        // a corrupt payload shouldn't crash the response.
        if (value.Kind == DataKind.Json && !value.IsArray)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
            try
            {
                string text = CborJsonCodec.DecodeToJsonText(bytes);
                return new JsonCell("json", Text: text);
            }
            catch
            {
                return new JsonCell("text", Text: $"<json decode failed; {bytes.Length:N0} bytes>");
            }
        }

        // Structured shapes (Struct, Array<Struct>, Array<scalar>, Array<String>)
        // route to the front-end's JSON tree renderer so the user gets a
        // collapsible, copyable view with field-name-aware rendering rather than
        // a one-line {f0: ..., f1: ...} blob.
        if (ShouldRouteToJson(value))
        {
            object? tree = BuildJsonNode(value, arena, registry, types);
            string text = JsonSerializer.Serialize(tree, JsonOpts);
            return new JsonCell("json", Text: text);
        }

        return new JsonCell("text", Text: FormatText(value, arena, registry, types));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        // The front-end's JSON.parse handles whatever we emit; default escaping
        // is fine. NumberHandling stays strict — non-finite doubles get serialized
        // as strings via the BuildJsonNode fallbacks below.
    };

    /// <summary>
    /// True when <paramref name="value"/> is a Struct or an array whose elements
    /// can be rendered as JSON cleanly (struct, scalar, string). Binary kinds
    /// (Image / Audio / Video) and their typed-array forms still go through the
    /// text path — base64 inside a JSON tree is unwieldy and the dedicated
    /// "media" cell already covers single-blob rendering.
    /// </summary>
    private static bool ShouldRouteToJson(DataValue value)
    {
        if (value.IsByteArrayKind) return false;          // UInt8[] → media or hex preview
        if (value.Kind == DataKind.Image
            || value.Kind == DataKind.Audio
            || value.Kind == DataKind.Video) return false; // single-blob already routes elsewhere
        if (value.Kind == DataKind.Struct) return true;
        if (value.IsArray) return true;
        return false;
    }

    /// <summary>
    /// Recursively builds an <see cref="object"/>-tree (primitives,
    /// <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c>)
    /// suitable for handing to <see cref="JsonSerializer.Serialize"/>.
    /// Struct field names come from the per-element TypeId via the
    /// <see cref="TypeRegistry"/>; missing names fall back to <c>fN</c>.
    /// </summary>
    private static object? BuildJsonNode(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types)
    {
        if (value.IsNull) return null;

        if (value.IsArray)
        {
            return BuildJsonArrayNode(value, arena, registry, types);
        }

        if (value.Kind == DataKind.Struct)
        {
            DataValue[] fieldValues = value.AsStruct(arena);
            TypeDescriptor? typeDesc = value.TypeId != 0 ? types?.GetDescriptor(value.TypeId) : null;
            Dictionary<string, object?> obj = new(capacity: fieldValues.Length, StringComparer.Ordinal);
            for (int i = 0; i < fieldValues.Length; i++)
            {
                string name = typeDesc?.Fields is { } tFields && i < tFields.Count
                    ? tFields[i].Name
                    : $"f{i}";
                // Disambiguate clashes (rare — duplicate field name in a struct
                // shape). Avoids a Dictionary key collision throwing mid-render.
                if (obj.ContainsKey(name)) name = $"{name}_{i}";
                obj[name] = BuildJsonNode(fieldValues[i], arena, registry, types);
            }
            return obj;
        }

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.UInt8 => (int)value.AsUInt8(),
            DataKind.Int8 => (int)value.AsInt8(),
            DataKind.UInt16 => (int)value.AsUInt16(),
            DataKind.Int16 => (int)value.AsInt16(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int32 => value.AsInt32(),
            // 64-bit ints can exceed JS Number.MAX_SAFE_INTEGER. Emit as number;
            // the front-end's JSON.parse preserves precision into a regular
            // Number even for values near the boundary, and the tree renderer
            // shows the .toString() form. If precision becomes a real issue,
            // promote to string here.
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.Float32 => Float32ToJson(value.AsFloat32()),
            DataKind.Float64 => Float64ToJson(value.AsFloat64()),
            DataKind.Decimal => value.AsDecimal(),
            DataKind.Date => value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DataKind.DateTime => value.AsDateTime().ToString("O", CultureInfo.InvariantCulture),
            DataKind.Time => value.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DataKind.Duration => value.AsDuration().ToString(),
            DataKind.Uuid => value.AsUuid().ToString(),
            DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena, registry),
            // Single-blob kinds inside a struct field: surface a placeholder so
            // the JSON tree stays readable. The dedicated "media" cell handles
            // top-level blobs; nested blobs are rare and base64 would dominate
            // the rendered tree.
            DataKind.Image => $"<image: {value.AsByteSpan(arena, registry).Length:N0} bytes>",
            DataKind.Audio => $"<audio: {value.AsByteSpan(arena, registry).Length:N0} bytes>",
            DataKind.Video => $"<video: {value.AsByteSpan(arena, registry).Length:N0} bytes>",
            DataKind.Json => DecodeJsonNodeOrFallback(value, arena, registry),
            DataKind.Type => value.FormatType(types),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static List<object?> BuildJsonArrayNode(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types)
    {
        // Struct arrays: each element is a self-describing Struct DataValue —
        // recurse into BuildJsonNode for each.
        if (value.Kind == DataKind.Struct)
        {
            DataValue[] elements = value.AsStructArray(arena, registry);
            List<object?> arr = new(elements.Length);
            for (int i = 0; i < elements.Length; i++)
            {
                arr.Add(BuildJsonNode(elements[i], arena, registry, types));
            }
            return arr;
        }

        // String arrays: cheap, emit as an array of JSON strings.
        if (value.Kind == DataKind.String)
        {
            string[] strings = value.AsStringArray(arena, registry);
            List<object?> arr = new(strings.Length);
            for (int i = 0; i < strings.Length; i++) arr.Add(strings[i]);
            return arr;
        }

        // Fixed-width primitive arrays: read as a typed span, box per element.
        return value.Kind switch
        {
            DataKind.Boolean => CollectPrimitiveArray<byte>(value, arena, registry, b => (object)(b != 0)),
            DataKind.UInt8 => CollectPrimitiveArray<byte>(value, arena, registry, b => (object)(int)b),
            DataKind.Int8 => CollectPrimitiveArray<sbyte>(value, arena, registry, b => (object)(int)b),
            DataKind.UInt16 => CollectPrimitiveArray<ushort>(value, arena, registry, v => (object)(int)v),
            DataKind.Int16 => CollectPrimitiveArray<short>(value, arena, registry, v => (object)(int)v),
            DataKind.UInt32 => CollectPrimitiveArray<uint>(value, arena, registry, v => (object)v),
            DataKind.Int32 => CollectPrimitiveArray<int>(value, arena, registry, v => (object)v),
            DataKind.UInt64 => CollectPrimitiveArray<ulong>(value, arena, registry, v => (object)v),
            DataKind.Int64 => CollectPrimitiveArray<long>(value, arena, registry, v => (object)v),
            DataKind.Float32 => CollectPrimitiveArray<float>(value, arena, registry, v => Float32ToJson(v)),
            DataKind.Float64 => CollectPrimitiveArray<double>(value, arena, registry, v => Float64ToJson(v)),
            // Image / Audio / Video arrays not handled here — ShouldRouteToJson
            // skips binary-element arrays, so we should never reach them.
            _ => new List<object?> { $"<Array<{value.Kind}> not yet renderable as JSON>" },
        };
    }

    private static List<object?> CollectPrimitiveArray<T>(
        DataValue value, Arena arena, SidecarRegistry registry, Func<T, object> box)
        where T : unmanaged
    {
        ReadOnlySpan<T> span = value.AsArraySpan<T>(arena, registry);
        List<object?> arr = new(span.Length);
        for (int i = 0; i < span.Length; i++) arr.Add(box(span[i]));
        return arr;
    }

    /// <summary>
    /// JSON's number type doesn't permit NaN/Infinity. Emit those as strings so
    /// the tree stays parseable. Finite values pass through as doubles.
    /// </summary>
    private static object Float32ToJson(float v) =>
        float.IsFinite(v) ? v : v.ToString("G", CultureInfo.InvariantCulture);

    private static object Float64ToJson(double v) =>
        double.IsFinite(v) ? v : v.ToString("G", CultureInfo.InvariantCulture);

    /// <summary>
    /// Decodes a Json-kind cell's CBOR payload to its parsed JSON node so the
    /// containing struct serialises with the inner JSON inline rather than as
    /// an opaque escaped string. Falls back to a placeholder on decode failure.
    /// </summary>
    private static object? DecodeJsonNodeOrFallback(
        DataValue value, Arena arena, SidecarRegistry registry)
    {
        ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
        try
        {
            string jsonText = CborJsonCodec.DecodeToJsonText(bytes);
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            return JsonElementToObject(doc.RootElement);
        }
        catch
        {
            return $"<json decode failed; {bytes.Length:N0} bytes>";
        }
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => el.GetRawText(),
    };

    private static string FormatText(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types = null)
    {
        if (value.IsNull) return "NULL";

        if (value.IsArray)
        {
            return FormatArray(value, arena, registry, types);
        }

        if (value.Kind == DataKind.Struct)
        {
            DataValue[] fieldValues = value.AsStruct(arena);
            TypeDescriptor? typeDesc = value.TypeId != 0 ? types?.GetDescriptor(value.TypeId) : null;
            string[] parts = new string[fieldValues.Length];
            for (int i = 0; i < fieldValues.Length; i++)
            {
                string name = typeDesc?.Fields is { } tFields && i < tFields.Count
                    ? tFields[i].Name
                    : $"f{i}";
                parts[i] = $"{name}: {FormatText(fieldValues[i], arena, registry, types)}";
            }
            return "{" + string.Join(", ", parts) + "}";
        }

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean() ? "true" : "false",
            DataKind.UInt8 => value.AsUInt8().ToString(CultureInfo.InvariantCulture),
            DataKind.Int8 => value.AsInt8().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt16 => value.AsUInt16().ToString(CultureInfo.InvariantCulture),
            DataKind.Int16 => value.AsInt16().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt32 => value.AsUInt32().ToString(CultureInfo.InvariantCulture),
            DataKind.Int32 => value.AsInt32().ToString(CultureInfo.InvariantCulture),
            DataKind.UInt64 => value.AsUInt64().ToString(CultureInfo.InvariantCulture),
            DataKind.Int64 => value.AsInt64().ToString(CultureInfo.InvariantCulture),
            DataKind.Float32 => value.AsFloat32().ToString("G", CultureInfo.InvariantCulture),
            DataKind.Float64 => value.AsFloat64().ToString("G", CultureInfo.InvariantCulture),
            DataKind.Decimal => value.AsDecimal().ToString(CultureInfo.InvariantCulture),
            DataKind.Date => value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DataKind.DateTime => value.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DataKind.Time => value.AsTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DataKind.Duration => value.AsDuration().ToString(),
            DataKind.Uuid => value.AsUuid().ToString(),
            DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena, registry),
            _ => value.ToString(),
        };
    }

    private static string FormatArray(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types = null)
    {
        if (value.Kind == DataKind.Struct)
        {
            // Each element is a self-describing Struct DataValue carrying its own
            // TypeId. No container-side ElementTypeId hop — read each row's
            // TypeId directly off the slot.
            DataValue[] elements = value.AsStructArray(arena, registry);
            string[] parts = new string[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                DataValue[] fields = elements[i].AsStruct(arena);
                parts[i] = FormatStructFromFields(fields, arena, registry, types, elements[i].TypeId);
            }
            return "[" + string.Join(", ", parts) + "]";
        }

        if (value.Kind == DataKind.String)
        {
            string[] strings = value.AsStringArray(arena, registry);
            string[] parts = new string[strings.Length];
            for (int i = 0; i < strings.Length; i++)
            {
                parts[i] = "\"" + strings[i] + "\"";
            }
            return "[" + string.Join(", ", parts) + "]";
        }

        return value.Kind switch
        {
            DataKind.Boolean => FormatPrimitiveArray<byte>(value, arena, registry, b => (b != 0).ToString()),
            DataKind.UInt8 => FormatPrimitiveArray<byte>(value, arena, registry, b => b.ToString(CultureInfo.InvariantCulture)),
            DataKind.Int8 => FormatPrimitiveArray<sbyte>(value, arena, registry, b => b.ToString(CultureInfo.InvariantCulture)),
            DataKind.UInt16 => FormatPrimitiveArray<ushort>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.Int16 => FormatPrimitiveArray<short>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.UInt32 => FormatPrimitiveArray<uint>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.Int32 => FormatPrimitiveArray<int>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.UInt64 => FormatPrimitiveArray<ulong>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.Int64 => FormatPrimitiveArray<long>(value, arena, registry, v => v.ToString(CultureInfo.InvariantCulture)),
            DataKind.Float32 => FormatPrimitiveArray<float>(value, arena, registry, v => v.ToString("G", CultureInfo.InvariantCulture)),
            DataKind.Float64 => FormatPrimitiveArray<double>(value, arena, registry, v => v.ToString("G", CultureInfo.InvariantCulture)),
            _ => $"<Array<{value.Kind}>>",
        };
    }

    private static string FormatStructFromFields(
        DataValue[] fieldValues, Arena arena, SidecarRegistry registry, TypeRegistry? types = null,
        ushort elementTypeId = 0)
    {
        TypeDescriptor? typeDesc = elementTypeId != 0 ? types?.GetDescriptor(elementTypeId) : null;
        string[] parts = new string[fieldValues.Length];
        for (int i = 0; i < fieldValues.Length; i++)
        {
            string name = typeDesc?.Fields is { } tFields && i < tFields.Count
                ? tFields[i].Name
                : $"f{i}";
            parts[i] = $"{name}: {FormatText(fieldValues[i], arena, registry, types)}";
        }
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string FormatPrimitiveArray<T>(
        DataValue value, Arena arena, SidecarRegistry registry, Func<T, string> format)
        where T : unmanaged
    {
        ReadOnlySpan<T> span = value.AsArraySpan<T>(arena, registry);
        string[] parts = new string[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            parts[i] = format(span[i]);
        }
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string DetectImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46
            && bytes[3] == 0x38 && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return "image/gif";
        }
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46
            && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42
            && bytes[11] == 0x50)
        {
            return "image/webp";
        }
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return "image/bmp";
        }
        return "application/octet-stream";
    }

    private static string DetectAudioMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x41 && bytes[10] == 0x56 && bytes[11] == 0x45)
        {
            return "audio/wav";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x66 && bytes[1] == 0x4C
            && bytes[2] == 0x61 && bytes[3] == 0x43)
        {
            return "audio/flac";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x4F && bytes[1] == 0x67
            && bytes[2] == 0x67 && bytes[3] == 0x53)
        {
            return "audio/ogg";
        }
        if (bytes.Length >= 3 && bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
        {
            return "audio/mpeg";
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
        {
            return "audio/mpeg";
        }
        if (bytes.Length >= 12
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            return "audio/mp4";
        }
        return "application/octet-stream";
    }

    private static string DetectVideoMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12
            && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            return "video/mp4";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45
            && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            return "video/webm";
        }
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x41 && bytes[9] == 0x56 && bytes[10] == 0x49 && bytes[11] == 0x20)
        {
            return "video/x-msvideo";
        }
        return "application/octet-stream";
    }
}
