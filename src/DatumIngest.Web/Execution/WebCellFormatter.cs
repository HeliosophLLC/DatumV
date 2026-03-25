using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions.Json;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Web.Execution;

internal static class WebCellFormatter
{
    public static JsonCell Format(
        DataValue value,
        Arena arena,
        SidecarRegistry registry,
        TypeRegistry? types = null,
        TypeIdTranslationTable? translations = null)
    {
        if (value.IsNull)
        {
            return new JsonCell("null");
        }

        // Array<Image> → render as a row of thumbnails on the front-end, each
        // independently clickable for the lightbox. Has to be checked before
        // the single-blob branch below because Image + IsArray would otherwise
        // hit the AsByteSpan path (which is single-blob only) and read garbage.
        // Audio / Video arrays not currently constructed by any code path; if
        // they show up later, add the matching AsAudioArray / AsVideoArray
        // accessors in DataValue and extend this branch.
        if (value.IsArray && value.Kind == DataKind.Image)
        {
            byte[][] elements = value.AsImageArray(arena, registry);
            JsonMediaItem[] items = new JsonMediaItem[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                byte[] bytes = elements[i];
                items[i] = new JsonMediaItem(DetectImageMime(bytes), Convert.ToBase64String(bytes));
            }
            return new JsonCell("media_array", Items: items);
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

        if (value.Kind == DataKind.PointCloud)
        {
            return FormatPointCloud(value, arena, registry);
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

        // Large fixed-width numeric arrays: ship raw little-endian bytes
        // + server-computed stats instead of a `[0.123, 0.456, ...]` JSON
        // string. A 384×384 depth tensor is ~1.4 MB as JSON text and turns
        // into a similarly-sized DOM text node per cell; the binary form is
        // ~590 KB on the wire and decodes to a Float32Array in microseconds.
        // The threshold preserves the JSON-tree path for small arrays where
        // inline inspection beats a stats card. Returns null when the array
        // is below threshold, so we fall through to the JSON-tree path.
        if (IsBinaryNumericArrayKind(value))
        {
            JsonCell? binary = TryFormatNumericArray(value, arena, registry);
            if (binary is not null) return binary;
        }

        // Structured shapes (Struct, Array<Struct>, Array<scalar>, Array<String>)
        // route to the front-end's JSON tree renderer so the user gets a
        // collapsible, copyable view with field-name-aware rendering rather than
        // a one-line {f0: ..., f1: ...} blob.
        if (ShouldRouteToJson(value))
        {
            object? tree = BuildJsonNode(value, arena, registry, types, translations);
            string text = JsonSerializer.Serialize(tree, JsonOpts);
            return new JsonCell("json", Text: text);
        }

        return new JsonCell("text", Text: FormatText(value, arena, registry, types, translations));
    }

    /// <summary>
    /// Element-count threshold above which a fixed-width numeric array
    /// switches from JSON-text rendering to the binary `numeric_array`
    /// transport. Tuned by guesswork: arrays this size or smaller render
    /// inline as readable JSON without obvious freeze; above this, the
    /// quadratic-feeling cost of multi-MB DOM text nodes + canvas
    /// measureText dominates. Adjust if profiling pulls a clear knee.
    /// </summary>
    private const int NumericArrayBinaryThreshold = 256;

    /// <summary>
    /// True when <paramref name="value"/> is a flat fixed-width numeric
    /// array whose element kind the binary transport supports. UInt8
    /// arrays are excluded — they already route to the image-media path
    /// above (legacy byte-payload-as-image convention) and never reach
    /// this check. Size gating happens in <see cref="TryFormatNumericArray"/>
    /// where the typed span is already in hand.
    /// </summary>
    private static bool IsBinaryNumericArrayKind(DataValue value)
    {
        if (!value.IsArray) return false;
        return value.Kind switch
        {
            DataKind.Boolean
            or DataKind.Int8
            or DataKind.UInt16
            or DataKind.Int16
            or DataKind.UInt32
            or DataKind.Int32
            or DataKind.UInt64
            or DataKind.Int64
            or DataKind.Float32
            or DataKind.Float64 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Formats a fixed-width numeric array as a binary <c>numeric_array</c>
    /// cell, or returns <c>null</c> when the array is below the size
    /// threshold (caller falls back to the JSON-tree path). DataB64 carries
    /// raw little-endian element bytes — both ends run on x64 so
    /// <see cref="MemoryMarshal.AsBytes{T}"/> is a zero-copy reinterpret.
    /// </summary>
    private static JsonCell? TryFormatNumericArray(DataValue value, Arena arena, SidecarRegistry registry)
    {
        return value.Kind switch
        {
            DataKind.Boolean => BuildBoolBinary(value.AsArraySpan<byte>(arena, registry)),
            DataKind.Int8 => BuildSignedBinary<sbyte>(value.AsArraySpan<sbyte>(arena, registry), "i8", v => v),
            DataKind.UInt16 => BuildUnsignedBinary<ushort>(value.AsArraySpan<ushort>(arena, registry), "u16", v => v),
            DataKind.Int16 => BuildSignedBinary<short>(value.AsArraySpan<short>(arena, registry), "i16", v => v),
            DataKind.UInt32 => BuildUnsignedBinary<uint>(value.AsArraySpan<uint>(arena, registry), "u32", v => v),
            DataKind.Int32 => BuildSignedBinary<int>(value.AsArraySpan<int>(arena, registry), "i32", v => v),
            DataKind.UInt64 => BuildUnsignedBinary<ulong>(value.AsArraySpan<ulong>(arena, registry), "u64", v => v),
            DataKind.Int64 => BuildSignedBinary<long>(value.AsArraySpan<long>(arena, registry), "i64", v => v),
            DataKind.Float32 => BuildFloat32Binary(value.AsArraySpan<float>(arena, registry)),
            DataKind.Float64 => BuildFloat64Binary(value.AsArraySpan<double>(arena, registry)),
            _ => null,
        };
    }

    private static JsonCell? BuildBoolBinary(ReadOnlySpan<byte> span)
    {
        if (span.Length <= NumericArrayBinaryThreshold) return null;
        string b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(span));
        // Bools have no meaningful min/max/mean; surface count only.
        return new JsonCell(
            "numeric_array",
            DataB64: b64,
            ElementKind: "bool",
            Count: span.Length);
    }

    private static JsonCell? BuildSignedBinary<T>(ReadOnlySpan<T> span, string elementKind, Func<T, long> toLong)
        where T : unmanaged
    {
        if (span.Length <= NumericArrayBinaryThreshold) return null;
        string b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(span));
        long min = long.MaxValue, max = long.MinValue;
        double sum = 0;
        for (int i = 0; i < span.Length; i++)
        {
            long v = toLong(span[i]);
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        return new JsonCell(
            "numeric_array",
            DataB64: b64,
            ElementKind: elementKind,
            Count: span.Length,
            Min: min,
            Max: max,
            Mean: sum / span.Length);
    }

    private static JsonCell? BuildUnsignedBinary<T>(ReadOnlySpan<T> span, string elementKind, Func<T, ulong> toULong)
        where T : unmanaged
    {
        if (span.Length <= NumericArrayBinaryThreshold) return null;
        string b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(span));
        ulong min = ulong.MaxValue, max = ulong.MinValue;
        double sum = 0;
        for (int i = 0; i < span.Length; i++)
        {
            ulong v = toULong(span[i]);
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        return new JsonCell(
            "numeric_array",
            DataB64: b64,
            ElementKind: elementKind,
            Count: span.Length,
            Min: min,
            Max: max,
            Mean: sum / span.Length);
    }

    private static JsonCell? BuildFloat32Binary(ReadOnlySpan<float> span)
    {
        if (span.Length <= NumericArrayBinaryThreshold) return null;
        string b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(span));
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int finite = 0;
        for (int i = 0; i < span.Length; i++)
        {
            float v = span[i];
            if (!float.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            finite++;
        }
        return new JsonCell(
            "numeric_array",
            DataB64: b64,
            ElementKind: "f32",
            Count: span.Length,
            Min: finite > 0 ? min : null,
            Max: finite > 0 ? max : null,
            Mean: finite > 0 ? sum / finite : null);
    }

    private static JsonCell? BuildFloat64Binary(ReadOnlySpan<double> span)
    {
        if (span.Length <= NumericArrayBinaryThreshold) return null;
        string b64 = Convert.ToBase64String(MemoryMarshal.AsBytes(span));
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int finite = 0;
        for (int i = 0; i < span.Length; i++)
        {
            double v = span[i];
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            finite++;
        }
        return new JsonCell(
            "numeric_array",
            DataB64: b64,
            ElementKind: "f64",
            Count: span.Length,
            Min: finite > 0 ? min : null,
            Max: finite > 0 ? max : null,
            Mean: finite > 0 ? sum / finite : null);
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
            || value.Kind == DataKind.Video
            || value.Kind == DataKind.PointCloud) return false; // single-blob already routes elsewhere
        if (value.Kind == DataKind.Struct) return true;
        if (value.IsArray) return true;
        return false;
    }

    /// <summary>
    /// Maximum uncompressed PointCloud blob size accepted by the inline transport.
    /// A 50 MB cap covers a 1920×1080 colored cloud (~33 MB) with headroom, and
    /// guards against streaming a multi-gigabyte cell that would crash the
    /// browser's JSON parser. Above this threshold, callers should decimate or
    /// project to a smaller depth resolution before requesting display.
    /// </summary>
    private const int PointCloudInlineCapBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Formats a single PointCloud value for the wire: parses the header for
    /// metadata-only summary fields, gzips the raw blob, base64-encodes the
    /// compressed bytes. The front-end uses the metadata to render a cell-grid
    /// thumbnail without decoding the blob, and pipes the base64 through
    /// <c>DecompressionStream("gzip")</c> when the user opens the 3D viewer.
    /// </summary>
    private static JsonCell FormatPointCloud(DataValue value, Arena arena, SidecarRegistry registry)
    {
        ReadOnlySpan<byte> blob = value.AsByteSpan(arena, registry);
        if (blob.Length > PointCloudInlineCapBytes)
        {
            return new JsonCell(
                "text",
                Text: $"<PointCloud too large to display: {blob.Length:N0} bytes; "
                    + $"inline transport cap is {PointCloudInlineCapBytes:N0} bytes>");
        }

        PointCloudHeader header = PointCloudHeader.Read(blob);

        using MemoryStream compressed = new(capacity: blob.Length / 2);
        using (GZipStream gz = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(blob);
        }
        string dataB64 = Convert.ToBase64String(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));

        JsonPointCloudInfo info = new(
            PointCount: checked((int)header.PointCount),
            HasColor: header.HasColor,
            Width: checked((int)header.Width),
            Height: checked((int)header.Height),
            CoordinateFrame: header.CoordinateFrame.ToString());

        return new JsonCell(
            Kind: "pointcloud",
            DataB64: dataB64,
            Encoding: "gzip",
            PointCloud: info);
    }

    /// <summary>
    /// Recursively builds an <see cref="object"/>-tree (primitives,
    /// <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object?&gt;</c>)
    /// suitable for handing to <see cref="JsonSerializer.Serialize"/>.
    /// Struct field names come from the per-element TypeId via the
    /// <see cref="TypeRegistry"/>; missing names fall back to <c>fN</c>.
    /// </summary>
    private static object? BuildJsonNode(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types,
        TypeIdTranslationTable? translations)
    {
        if (value.IsNull) return null;

        if (value.IsArray)
        {
            return BuildJsonArrayNode(value, arena, registry, types, translations);
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
                obj[name] = BuildJsonNode(fieldValues[i], arena, registry, types, translations);
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
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types,
        TypeIdTranslationTable? translations)
    {
        // Struct arrays: each element is a self-describing Struct DataValue —
        // recurse into BuildJsonNode for each. The translator turns sidecar-
        // resident on-disk TypeIds back into runtime registry ids before the
        // recursion looks up field names.
        if (value.Kind == DataKind.Struct)
        {
            DataValue[] elements = value.AsStructArray(arena, registry, translations);
            List<object?> arr = new(elements.Length);
            for (int i = 0; i < elements.Length; i++)
            {
                arr.Add(BuildJsonNode(elements[i], arena, registry, types, translations));
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
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types = null,
        TypeIdTranslationTable? translations = null)
    {
        if (value.IsNull) return "NULL";

        if (value.IsArray)
        {
            return FormatArray(value, arena, registry, types, translations);
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
                parts[i] = $"{name}: {FormatText(fieldValues[i], arena, registry, types, translations)}";
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
            // Type values: render the structural description so cells like
            // typeof({a: 'test'}) show as "Struct{a: String}" instead of the
            // bare kind name. FormatType degrades to the kind name when the
            // registry can't resolve the carried TypeId.
            DataKind.Type => value.FormatType(types),
            _ => value.ToString(),
        };
    }

    private static string FormatArray(
        DataValue value, Arena arena, SidecarRegistry registry, TypeRegistry? types = null,
        TypeIdTranslationTable? translations = null)
    {
        if (value.Kind == DataKind.Struct)
        {
            // Each element is a self-describing Struct DataValue carrying its own
            // TypeId. The translator turns sidecar-resident on-disk ids back
            // into runtime registry ids so field-name lookup hits the right
            // descriptor.
            DataValue[] elements = value.AsStructArray(arena, registry, translations);
            string[] parts = new string[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                DataValue[] fields = elements[i].AsStruct(arena);
                parts[i] = FormatStructFromFields(fields, arena, registry, types, elements[i].TypeId, translations);
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
        ushort elementTypeId = 0,
        TypeIdTranslationTable? translations = null)
    {
        TypeDescriptor? typeDesc = elementTypeId != 0 ? types?.GetDescriptor(elementTypeId) : null;
        string[] parts = new string[fieldValues.Length];
        for (int i = 0; i < fieldValues.Length; i++)
        {
            string name = typeDesc?.Fields is { } tFields && i < tFields.Count
                ? tFields[i].Name
                : $"f{i}";
            parts[i] = $"{name}: {FormatText(fieldValues[i], arena, registry, types, translations)}";
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
