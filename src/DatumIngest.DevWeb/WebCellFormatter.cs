using System.Globalization;
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

        return new JsonCell("text", Text: FormatText(value, arena, registry, types));
    }

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
            ushort elementTypeId = value.TypeId;
            DataValue[][] rows = value.AsStructArray(arena, registry);
            string[] parts = new string[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                parts[i] = FormatStructFromFields(rows[i], arena, registry, types, elementTypeId);
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
