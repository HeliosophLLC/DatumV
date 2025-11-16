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

    public static string FormatValue(
        DataValue value,
        Arena arena,
        SidecarRegistry? registry,
        IReadOnlyList<ColumnInfo>? structFields = null)
    {
        if (structFields is not null && value.Kind == DataKind.Struct)
        {
            return FormatStructValue(value, arena, registry, structFields);
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
            DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena),
            DataKind.JsonValue => value.IsInline ? value.AsString() : value.AsString(arena),
            DataKind.Image or DataKind.UInt8Array => FormatBlobPreview(value, arena, registry),
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
        IReadOnlyList<ColumnInfo>? fields)
    {
        DataValue[] fieldValues = value.AsStruct();
        IEnumerable<string> parts = fieldValues.Select((fieldValue, index) =>
        {
            string name = fields is not null && index < fields.Count ? fields[index].Name : $"f{index}";
            IReadOnlyList<ColumnInfo>? nestedFields =
                fields is not null && index < fields.Count ? fields[index].Fields : null;
            string formatted = fieldValue.IsNull
                ? "NULL"
                : FormatValue(fieldValue, arena, registry, nestedFields);
            return $"{name}: {formatted}";
        });
        return $"{{{string.Join(", ", parts)}}}";
    }
}
