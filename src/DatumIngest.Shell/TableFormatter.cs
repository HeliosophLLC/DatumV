using System.Text;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.Shell;

/// <summary>
/// Renders rows into psql-style aligned tables. The shell drives row buffering
/// and pagination itself; this class only formats one page worth of already-
/// converted string cells, plus shared helpers for converting individual
/// <see cref="DataValue"/>s to display strings.
/// </summary>
internal static class TableFormatter
{
    /// <summary>
    /// Maximum display width for any single column. Tracks the terminal width
    /// so a wide cell (e.g. an LLM response) gets the full screen rather than
    /// being clipped at an arbitrary 40-character cap. Falls back to 200 when
    /// no console is attached (redirected output, pipes, tests).
    /// </summary>
    private static int MaxColumnWidth
    {
        get
        {
            try
            {
                int width = Console.WindowWidth;
                return width > 0 ? Math.Max(40, width - 4) : 200;
            }
            catch (IOException)
            {
                return 200;
            }
        }
    }

    /// <summary>
    /// Renders one page of rows. <paramref name="cells"/> holds the already-
    /// formatted string for every cell in the page, indexed [row][column].
    /// When <paramref name="printHeader"/> is <see langword="true"/>, the
    /// column-name header and separator line are emitted before the rows.
    /// </summary>
    public static void RenderPage(
        IReadOnlyList<string[]> cells,
        Schema schema,
        bool printHeader,
        TextWriter writer)
    {
        int columnCount = schema.Columns.Count;
        if (columnCount == 0)
        {
            writer.WriteLine("(empty result)");
            return;
        }

        int[] widths = new int[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            widths[i] = schema.Columns[i].Name.Length;
        }

        foreach (string[] row in cells)
        {
            for (int i = 0; i < columnCount; i++)
            {
                if (row[i].Length > widths[i])
                {
                    widths[i] = row[i].Length;
                }
            }
        }

        bool[] rightAlign = new bool[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            if (widths[i] > MaxColumnWidth)
            {
                widths[i] = MaxColumnWidth;
            }
            rightAlign[i] = IsNumericScalar(schema.Columns[i].Kind);
        }

        StringBuilder line = new();
        if (printHeader)
        {
            FormatRow(line, schema.Columns.Select(c => c.Name).ToArray(), widths, rightAlign);
            writer.WriteLine(line.ToString());

            line.Clear();
            for (int i = 0; i < columnCount; i++)
            {
                if (i > 0) line.Append("-+-");
                line.Append('-', widths[i]);
            }
            writer.WriteLine(line.ToString());
        }

        foreach (string[] row in cells)
        {
            line.Clear();
            FormatRow(line, row, widths, rightAlign);
            writer.WriteLine(line.ToString());
        }
    }

    private static void FormatRow(StringBuilder builder, string[] values, int[] widths, bool[] rightAlign)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(" | ");

            string value = values[i];
            if (value.Length > widths[i])
            {
                value = string.Concat(value.AsSpan(0, widths[i] - 1), "~");
            }

            if (rightAlign[i])
            {
                builder.Append(value.PadLeft(widths[i]));
            }
            else
            {
                builder.Append(value.PadRight(widths[i]));
            }
        }
    }

    /// <summary>
    /// Maximum number of array elements rendered inline before the tail is
    /// elided as <c>... (N more)</c>. Long detection arrays (e.g. YOLO with
    /// 20+ boxes) blow past column width otherwise; truncating keeps the
    /// table readable while still showing what shape the value has.
    /// </summary>
    private const int InlineArrayPreviewCount = 4;

    public static string FormatValue(
        DataValue value,
        Arena arena,
        SidecarRegistry? registry,
        IReadOnlyList<ColumnInfo>? structFields = null,
        TypeRegistry? types = null)
    {
        // Byte arrays render as a hex preview regardless of how the column was
        // declared — the IsArray-aware branch below would format them as a
        // numeric array, which is unreadable for image/blob payloads.
        if (value.IsByteArrayKind)
        {
            return FormatBlobPreview(value, arena, registry);
        }

        if (value.IsArray)
        {
            return FormatArrayValue(value, arena, registry, structFields, types);
        }

        if (value.Kind == DataKind.Struct)
        {
            return FormatStructValue(value, arena, registry, structFields, types);
        }

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean() ? "true" : "false",
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.Int8 => value.AsInt8().ToString(),
            DataKind.UInt16 => value.AsUInt16().ToString(),
            DataKind.Int16 => value.AsInt16().ToString(),
            DataKind.UInt32 => value.AsUInt32().ToString(),
            DataKind.Int32 => value.AsInt32().ToString(),
            DataKind.UInt64 => value.AsUInt64().ToString(),
            DataKind.Int64 => value.AsInt64().ToString(),
            DataKind.Float32 => value.AsFloat32().ToString("G"),
            DataKind.Float64 => value.AsFloat64().ToString("G"),
            DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
            DataKind.DateTime => value.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
            DataKind.Time => value.AsTime().ToString("HH:mm:ss"),
            DataKind.Duration => value.AsDuration().ToString(),
            DataKind.Uuid => value.AsUuid().ToString(),
            DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena, registry),
            DataKind.Image => FormatBlobPreview(value, arena, registry),
            DataKind.Audio => FormatBlobPreview(value, arena, registry),
            DataKind.Video => FormatBlobPreview(value, arena, registry),
            DataKind.Json => FormatJsonPreview(value, arena, registry),
            _ => value.ToDisplayString(),
        };
    }

    private static string FormatBlobPreview(DataValue value, Arena arena, SidecarRegistry? registry)
    {
        const int PreviewBytes = 8;
        ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
        int previewLength = Math.Min(PreviewBytes, bytes.Length);
        string hex = Convert.ToHexString(bytes[..previewLength]);
        return bytes.Length > PreviewBytes
            ? $"0x{hex}... ({bytes.Length:N0} bytes)"
            : $"0x{hex} ({bytes.Length:N0} bytes)";
    }

    /// <summary>
    /// Decodes the canonical CBOR payload back to JSON text and returns a
    /// truncated preview suitable for table display. The byte size is shown
    /// in parentheses so the user knows the on-disk footprint regardless of
    /// the truncation. Falls back to the hex preview if decode throws — that
    /// shouldn't happen for engine-produced Json values, but a corrupted
    /// payload shouldn't crash the formatter.
    /// </summary>
    private static string FormatJsonPreview(DataValue value, Arena arena, SidecarRegistry? registry)
    {
        const int PreviewChars = 60;
        ReadOnlySpan<byte> bytes = value.AsByteSpan(arena, registry);
        try
        {
            string json = DatumIngest.Functions.Json.CborJsonCodec.DecodeToJsonText(bytes);
            string trimmed = json.Length > PreviewChars
                ? json[..PreviewChars] + "..."
                : json;
            return $"{trimmed} ({bytes.Length:N0} bytes)";
        }
        catch (Exception)
        {
            return FormatBlobPreview(value, arena, registry);
        }
    }

    private static bool IsNumericScalar(DataKind kind) => kind is
        DataKind.Int8 or DataKind.UInt8 or
        DataKind.Int16 or DataKind.UInt16 or
        DataKind.Int32 or DataKind.UInt32 or
        DataKind.Int64 or DataKind.UInt64 or
        DataKind.Float32 or DataKind.Float64;

    private static string FormatStructValue(
        DataValue value,
        Arena arena,
        SidecarRegistry? registry,
        IReadOnlyList<ColumnInfo>? fields,
        TypeRegistry? types = null)
    {
        DataValue[] fieldValues = value.AsStruct(arena);
        // Fall back to the registry-resident TypeDescriptor when the caller
        // doesn't have a ColumnInfo (procedural FOR-loop @row, model output
        // without schema column, struct returned from index access). The
        // TypeDescriptor carries the field names stamped at construction time.
        TypeDescriptor? typeDesc = types is not null && value.TypeId != 0
            ? types.GetDescriptor(value.TypeId)
            : null;
        IEnumerable<string> parts = fieldValues.Select((fieldValue, index) =>
        {
            string name = fields is not null && index < fields.Count
                ? fields[index].Name
                : typeDesc?.Fields is { } tdFields && index < tdFields.Count
                    ? tdFields[index].Name
                    : $"f{index}";
            IReadOnlyList<ColumnInfo>? nestedFields =
                fields is not null && index < fields.Count ? fields[index].Fields : null;
            string formatted = fieldValue.IsNull
                ? "NULL"
                : FormatValue(fieldValue, arena, registry, nestedFields, types);
            return $"{name}: {formatted}";
        });
        return $"{{{string.Join(", ", parts)}}}";
    }

    /// <summary>
    /// Renders an <c>Array&lt;Kind&gt;</c> value as <c>[a, b, c, ... (N more)]</c>.
    /// Element formatting recurses through <see cref="FormatValue"/> so
    /// <c>Array&lt;Struct&gt;</c> stays readable (each element is itself a
    /// <c>{f0: ..., f1: ...}</c>). Truncates after
    /// <see cref="InlineArrayPreviewCount"/> elements — an unbounded YOLO
    /// detection array would otherwise render hundreds of characters per row.
    /// </summary>
    private static string FormatArrayValue(
        DataValue value,
        Arena arena,
        SidecarRegistry? registry,
        IReadOnlyList<ColumnInfo>? structFields,
        TypeRegistry? types = null)
    {
        if (value.Kind == DataKind.Struct)
        {
            // Each element is a self-describing Struct DataValue carrying its own
            // TypeId in the slot's reserved bytes. Pull fields per element via
            // AsStruct and format with the element's own TypeId — no container-
            // side ElementTypeId hop needed.
            DataValue[] elements = value.AsStructArray(arena, registry);
            return FormatArrayElements(
                elements.Length,
                index => FormatStructFromFieldArray(
                    elements[index].AsStruct(arena),
                    arena,
                    registry,
                    structFields,
                    types,
                    elements[index].TypeId));
        }

        if (value.Kind == DataKind.String)
        {
            string[] strings = value.AsStringArray(arena, registry);
            return FormatArrayElements(strings.Length, index => $"\"{strings[index]}\"");
        }

        if (value.Kind == DataKind.Image)
        {
            byte[][] images = value.AsImageArray(arena, registry);
            return FormatArrayElements(
                images.Length,
                index =>
                {
                    int len = images[index].Length;
                    string preview = Convert.ToHexString(images[index].AsSpan(0, Math.Min(8, len)));
                    return $"0x{preview}{(len > 8 ? "..." : "")} ({len:N0} bytes)";
                });
        }

        // Fixed-width primitive arrays — packed in arena/inline bytes; fall
        // through to ToDisplayString for kinds the formatter doesn't yet
        // unpack here (Decimal, Int128, Uuid arrays etc.). The supported
        // kinds cover everything models currently emit.
        return value.Kind switch
        {
            DataKind.Boolean => FormatPrimitiveArray<byte>(value, arena, registry, b => (b != 0).ToString()),
            DataKind.UInt8 => FormatPrimitiveArray<byte>(value, arena, registry, b => b.ToString()),
            DataKind.Int8 => FormatPrimitiveArray<sbyte>(value, arena, registry, b => b.ToString()),
            DataKind.UInt16 => FormatPrimitiveArray<ushort>(value, arena, registry, v => v.ToString()),
            DataKind.Int16 => FormatPrimitiveArray<short>(value, arena, registry, v => v.ToString()),
            DataKind.UInt32 => FormatPrimitiveArray<uint>(value, arena, registry, v => v.ToString()),
            DataKind.Int32 => FormatPrimitiveArray<int>(value, arena, registry, v => v.ToString()),
            DataKind.UInt64 => FormatPrimitiveArray<ulong>(value, arena, registry, v => v.ToString()),
            DataKind.Int64 => FormatPrimitiveArray<long>(value, arena, registry, v => v.ToString()),
            DataKind.Float32 => FormatPrimitiveArray<float>(value, arena, registry, v => v.ToString("G")),
            DataKind.Float64 => FormatPrimitiveArray<double>(value, arena, registry, v => v.ToString("G")),
            _ => $"<Array<{value.Kind}>>",
        };
    }

    private static string FormatPrimitiveArray<T>(
        DataValue value, Arena arena, SidecarRegistry? registry, Func<T, string> format)
        where T : unmanaged
    {
        ReadOnlySpan<T> elements = value.AsArraySpan<T>(arena, registry);
        // Span captured by ref struct can't escape into a closure — eagerly
        // materialize a small preview window before delegating to the shared
        // bracket/truncation helper.
        int previewCount = Math.Min(elements.Length, InlineArrayPreviewCount);
        string[] previews = new string[previewCount];
        for (int i = 0; i < previewCount; i++)
        {
            previews[i] = format(elements[i]);
        }
        return FormatArrayElements(elements.Length, index => previews[index]);
    }

    private static string FormatStructFromFieldArray(
        DataValue[] fieldValues,
        Arena arena,
        SidecarRegistry? registry,
        IReadOnlyList<ColumnInfo>? fields,
        TypeRegistry? types = null,
        ushort elementTypeId = 0)
    {
        TypeDescriptor? typeDesc = types is not null && elementTypeId != 0
            ? types.GetDescriptor(elementTypeId)
            : null;
        IEnumerable<string> parts = fieldValues.Select((fv, i) =>
        {
            string name = fields is not null && i < fields.Count
                ? fields[i].Name
                : typeDesc?.Fields is { } tdFields && i < tdFields.Count
                    ? tdFields[i].Name
                    : $"f{i}";
            IReadOnlyList<ColumnInfo>? nested = fields is not null && i < fields.Count ? fields[i].Fields : null;
            string formatted = fv.IsNull ? "NULL" : FormatValue(fv, arena, registry, nested, types);
            return $"{name}: {formatted}";
        });
        return $"{{{string.Join(", ", parts)}}}";
    }

    /// <summary>
    /// Given the TypeId of an <c>Array&lt;Struct&gt;</c>, hops through the array
    /// descriptor's <see cref="TypeDescriptor.ElementTypeId"/> to return the
    /// element struct's TypeId. Returns 0 when not applicable.
    /// </summary>
    private static ushort ResolveArrayElementTypeId(ushort arrayTypeId, TypeRegistry? types)
    {
        if (arrayTypeId == 0 || types is null) return 0;
        TypeDescriptor? desc = types.GetDescriptor(arrayTypeId);
        if (desc is null || !desc.IsArray) return 0;
        return desc.ElementTypeId is { } eid ? (ushort)eid : (ushort)0;
    }

    /// <summary>
    /// Shared bracket + truncation wrapper. <paramref name="totalCount"/> is
    /// the array's logical length; <paramref name="renderElement"/> is invoked
    /// only for indices that survive the preview window.
    /// </summary>
    private static string FormatArrayElements(int totalCount, Func<int, string> renderElement)
    {
        if (totalCount == 0) return "[]";

        int previewCount = Math.Min(totalCount, InlineArrayPreviewCount);
        StringBuilder sb = new();
        sb.Append('[');
        for (int i = 0; i < previewCount; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(renderElement(i));
        }
        if (totalCount > previewCount)
        {
            sb.Append(", ... (").Append(totalCount - previewCount).Append(" more)");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
