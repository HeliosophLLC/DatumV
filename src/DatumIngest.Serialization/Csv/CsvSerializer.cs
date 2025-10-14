using System.Globalization;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Serializes a stream of <see cref="RowBatch"/> instances into CSV format.
/// Writes a header row followed by data rows. Strings containing the delimiter,
/// quotes, or newlines are quoted per RFC 4180.
/// </summary>
public sealed class CsvSerializer : IFormatSerializer
{
    private readonly OutputDescriptor _descriptor;

    /// <summary>Creates a serializer for the given output descriptor.</summary>
    public CsvSerializer(OutputDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 65536, leaveOpen: true);

        char delimiter = GetDelimiter(_descriptor.Options);
        bool writeHeader = GetHeaderOption(_descriptor.Options);

        bool headerWritten = false;

        await foreach (RowBatch batch in rows.WithCancellation(cancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];

                if (!headerWritten && writeHeader)
                {
                    WriteHeaderRow(writer, row.ColumnNames, delimiter);
                    headerWritten = true;
                }

                WriteDataRow(writer, row, delimiter, batch.Arena);
            }
        }
    }

    private static void WriteHeaderRow(StreamWriter writer, IReadOnlyList<string> names, char delimiter)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) writer.Write(delimiter);
            WriteField(writer, names[i], delimiter);
        }

        writer.WriteLine();
    }

    private static void WriteDataRow(StreamWriter writer, Row row, char delimiter, IValueStore store)
    {
        int fieldCount = row.FieldCount;

        for (int i = 0; i < fieldCount; i++)
        {
            if (i > 0) writer.Write(delimiter);

            DataValue value = row[i];

            if (value.IsNull)
                continue; // Empty field for nulls.

            WriteValue(writer, value, delimiter, store);
        }

        writer.WriteLine();
    }

    private static void WriteValue(StreamWriter writer, DataValue value, char delimiter, IValueStore store)
    {
        switch (value.Kind)
        {
            case DataKind.Boolean:
                writer.Write(value.AsBoolean() ? "true" : "false");
                break;

            case DataKind.Int8:
                writer.Write(value.AsInt8());
                break;
            case DataKind.Int16:
                writer.Write(value.AsInt16());
                break;
            case DataKind.Int32:
                writer.Write(value.AsInt32());
                break;
            case DataKind.Int64:
                writer.Write(value.AsInt64());
                break;

            case DataKind.UInt8:
                writer.Write(value.AsUInt8());
                break;
            case DataKind.UInt16:
                writer.Write(value.AsUInt16());
                break;
            case DataKind.UInt32:
                writer.Write(value.AsUInt32());
                break;
            case DataKind.UInt64:
                writer.Write(value.AsUInt64());
                break;

            case DataKind.Float32:
                writer.Write(value.AsFloat32().ToString("G", CultureInfo.InvariantCulture));
                break;
            case DataKind.Float64:
                writer.Write(value.AsFloat64().ToString("G", CultureInfo.InvariantCulture));
                break;

            case DataKind.Date:
                writer.Write(value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DataKind.DateTime:
                writer.Write(value.AsDateTime().ToString("O", CultureInfo.InvariantCulture));
                break;
            case DataKind.Time:
                writer.Write(value.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.Duration:
                writer.Write(value.AsDuration().ToString("c", CultureInfo.InvariantCulture));
                break;

            case DataKind.Uuid:
                writer.Write(value.AsUuid().ToString("D"));
                break;

            case DataKind.String:
                WriteField(writer, value.AsString(store), delimiter);
                break;

            case DataKind.JsonValue:
                WriteField(writer, value.AsJsonValue(store), delimiter);
                break;

            default:
                // Vector, Matrix, Tensor, Array, Struct, Image, UInt8Array — not CSV-friendly.
                // Write empty field rather than failing.
                break;
        }
    }

    /// <summary>
    /// Writes a string field, quoting per RFC 4180 if the value contains
    /// the delimiter, double quotes, or newlines.
    /// </summary>
    private static void WriteField(StreamWriter writer, ReadOnlySpan<char> value, char delimiter)
    {
        if (NeedsQuoting(value, delimiter))
        {
            writer.Write('"');
            // Write char-by-char, doubling quotes — avoids the string.Replace allocation.
            foreach (char c in value)
            {
                if (c == '"') writer.Write('"');
                writer.Write(c);
            }
            writer.Write('"');
        }
        else
        {
            writer.Write(value);
        }
    }

    private static bool NeedsQuoting(ReadOnlySpan<char> value, char delimiter)
    {
        foreach (char c in value)
        {
            if (c == delimiter || c == '"' || c == '\n' || c == '\r')
                return true;
        }

        return false;
    }

    private static char GetDelimiter(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("delimiter", out string? value) && value.Length > 0)
            return value[0];

        return ',';
    }

    private static bool GetHeaderOption(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("header", out string? value))
            return !value.Equals("false", StringComparison.OrdinalIgnoreCase);

        return true; // Default: write header.
    }
}
