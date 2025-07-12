namespace DatumQuery.Output.Writers;

using DatumQuery.Model;

/// <summary>
/// Writes query results to CSV files in RFC 4180 format.
/// </summary>
public sealed class CsvOutputWriter : IOutputWriter
{
    private readonly string? _filePath;
    private readonly Stream? _outputStream;
    private StreamWriter? _writer;
    private Schema? _schema;
    private long _rowsWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvOutputWriter"/> class that writes to a file.
    /// </summary>
    /// <param name="filePath">The output CSV file path.</param>
    public CsvOutputWriter(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CsvOutputWriter"/> class that writes to a stream.
    /// The caller retains ownership of the stream and is responsible for disposing it.
    /// </summary>
    /// <param name="outputStream">The stream to write CSV data to.</param>
    public CsvOutputWriter(Stream outputStream)
    {
        _outputStream = outputStream;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;

        if (_outputStream is not null)
        {
            _writer = new StreamWriter(_outputStream, encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
        }
        else
        {
            string? directory = Path.GetDirectoryName(_filePath!);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(_filePath!, append: false, encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

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

        if (_outputStream is not null)
        {
            long bytesWritten = _outputStream.CanSeek ? _outputStream.Position : 0;
            return new OutputSummary(_rowsWritten, bytesWritten, []);
        }

        long fileBytesWritten = File.Exists(_filePath) ? new FileInfo(_filePath!).Length : 0;
        return new OutputSummary(_rowsWritten, fileBytesWritten, [_filePath!]);
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
            DataKind.Vector => FormatVector(value.AsVector()),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Formats a float vector as a bracketed comma-separated list without LINQ.
    /// </summary>
    private static string FormatVector(float[] vector)
    {
        System.Text.StringBuilder builder = new();
        builder.Append('[');
        for (int index = 0; index < vector.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[index].ToString("G"));
        }

        builder.Append(']');
        return builder.ToString();
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
