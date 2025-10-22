using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Shell;

/// <summary>
/// Formats streaming rows into psql-style aligned tables with column headers,
/// separator lines, right-aligned numerics, and a row-count footer.
/// </summary>
internal sealed class TableFormatter
{
    /// <summary>Maximum number of rows buffered before truncating output.</summary>
    private const int MaxBufferedRows = 1000;

    /// <summary>Maximum display width for any single column.</summary>
    private const int MaxColumnWidth = 40;

    /// <summary>
    /// Formats an asynchronous stream of rows into a psql-style table written to a <see cref="TextWriter"/>.
    /// </summary>
    public async Task FormatAsync(IAsyncEnumerable<RowBatch> rows, Schema schema, TextWriter writer)
    {
        List<string[]> bufferedCells = new();
        int columnCount = schema.Columns.Count;
        bool truncated = false;

        await foreach (RowBatch batch in rows.ConfigureAwait(false))
        {
            for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
            {
                Row row = batch[rowIndex];
                if (bufferedCells.Count >= MaxBufferedRows)
                {
                    truncated = true;
                    continue;
                }

                string[] cells = new string[columnCount];
                for (int i = 0; i < columnCount; i++)
                {
                    DataValue value = row[i];
                    cells[i] = value.IsNull ? "NULL" : FormatValue(value, schema.Columns[i].Fields);
                }

                bufferedCells.Add(cells);
            }
            batch.Return();
        }

        if (columnCount == 0)
        {
            await writer.WriteLineAsync("(empty result)").ConfigureAwait(false);
            return;
        }

        int[] widths = new int[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            widths[i] = schema.Columns[i].Name.Length;
        }

        foreach (string[] cells in bufferedCells)
        {
            for (int i = 0; i < columnCount; i++)
            {
                if (cells[i].Length > widths[i])
                {
                    widths[i] = cells[i].Length;
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
        FormatRow(line, schema.Columns.Select(c => c.Name).ToArray(), widths, rightAlign);
        await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);

        line.Clear();
        for (int i = 0; i < columnCount; i++)
        {
            if (i > 0)
            {
                line.Append("-+-");
            }

            line.Append('-', widths[i]);
        }

        await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);

        foreach (string[] cells in bufferedCells)
        {
            line.Clear();
            FormatRow(line, cells, widths, rightAlign);
            await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);
        }

        string rowLabel = bufferedCells.Count == 1 ? "row" : "rows";
        string footer = truncated
            ? $"({bufferedCells.Count} {rowLabel}, truncated)"
            : $"({bufferedCells.Count} {rowLabel})";
        await writer.WriteLineAsync(footer).ConfigureAwait(false);
    }

    private static void FormatRow(StringBuilder builder, string[] values, int[] widths, bool[] rightAlign)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(" | ");
            }

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

    internal static string FormatValue(DataValue value, IReadOnlyList<ColumnInfo>? structFields = null)
    {
        if (structFields is not null && value.Kind == DataKind.Struct)
        {
            return FormatStructValue(value, structFields);
        }

        return value.ToDisplayString();
    }

    private static bool IsNumericScalar(DataKind kind) => kind is
        DataKind.Int8 or DataKind.UInt8 or
        DataKind.Int16 or DataKind.UInt16 or
        DataKind.Int32 or DataKind.UInt32 or
        DataKind.Int64 or DataKind.UInt64 or
        DataKind.Float32 or DataKind.Float64;

    private static string FormatStructValue(DataValue value, IReadOnlyList<ColumnInfo>? fields)
    {
        DataValue[] fieldValues = value.AsStruct();
        IEnumerable<string> parts = fieldValues.Select((fieldValue, index) =>
        {
            string name = fields is not null && index < fields.Count ? fields[index].Name : $"f{index}";
            string formatted = fieldValue.ToDisplayString();
            return $"{name}: {formatted}";
        });
        return $"{{{string.Join(", ", parts)}}}";
    }
}
