namespace Axon.QueryEngine.Output.Writers;

using Axon.QueryEngine.Model;

/// <summary>
/// Writes query results to CSV files in RFC 4180 format.
/// </summary>
public sealed class CsvOutputWriter : IOutputWriter
{
    private readonly string _filePath;
    private StreamWriter? _writer;
    private Schema? _schema;
    private long _rowsWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvOutputWriter"/> class.
    /// </summary>
    /// <param name="filePath">The output CSV file path.</param>
    public CsvOutputWriter(string filePath)
    {
        _filePath = filePath;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(_filePath, append: false, encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Write header
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (i > 0)
            {
                await _writer.WriteAsync(',');
            }

            await _writer.WriteAsync(EscapeField(schema.Columns[i].Name));
        }

        await _writer.WriteLineAsync();
    }

    /// <inheritdoc />
    public async Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        if (_writer is null || _schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (i > 0)
            {
                await _writer.WriteAsync(',');
            }

            DataValue value = row[i];
            string field = FormatValue(value);
            await _writer.WriteAsync(EscapeField(field));
        }

        await _writer.WriteLineAsync();
        _rowsWritten++;
    }

    /// <inheritdoc />
    public async Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken);
            await _writer.DisposeAsync();
            _writer = null;
        }

        long bytesWritten = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        return new OutputSummary(_rowsWritten, bytesWritten, [_filePath]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }
    }

    private static string FormatValue(DataValue value)
    {
        if (value.IsNull)
        {
            return "";
        }

        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar().ToString("G"),
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.String => value.AsString(),
            DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.JsonValue => value.AsJsonValue(),
            DataKind.Vector => "[" + string.Join(",", value.AsVector().Select(v => v.ToString("G"))) + "]",
            _ => value.ToString() ?? ""
        };
    }

    private static string EscapeField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}
