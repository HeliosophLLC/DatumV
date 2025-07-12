using System.Globalization;
using System.Runtime.CompilerServices;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads CSV files conforming to RFC 4180. Supports configurable delimiter,
/// automatic numeric column detection, projection pushdown, and byte-level
/// chunk measurement for index building.
/// </summary>
public sealed class CsvTableProvider : IChunkMeasuringProvider
{
    private const char DefaultDelimiter = ',';

    /// <summary>
    /// Byte-level scan buffer size for chunk measurement.
    /// </summary>
    private const int MeasurementBufferSize = 65536;

    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        char delimiter = GetDelimiter(descriptor);

        using StreamReader reader = new(descriptor.FilePath);
        string? headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null)
        {
            throw new InvalidOperationException($"CSV file '{descriptor.FilePath}' is empty (no header row).");
        }

        string[] headers = ParseCsvLine(headerLine, delimiter);

        // Read up to 100 rows to detect numeric columns
        DataKind[] kinds = new DataKind[headers.Length];
        Array.Fill(kinds, DataKind.Scalar); // Assume numeric until proven otherwise
        bool[] hasData = new bool[headers.Length];

        int sampledRows = 0;
        while (sampledRows < 100)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            string[] fields = ParseCsvLine(line, delimiter);
            for (int columnIndex = 0; columnIndex < Math.Min(fields.Length, headers.Length); columnIndex++)
            {
                string field = fields[columnIndex].Trim();
                if (field.Length == 0)
                {
                    continue;
                }

                hasData[columnIndex] = true;
                if (kinds[columnIndex] == DataKind.Scalar &&
                    !float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    kinds[columnIndex] = DataKind.String;
                }
            }

            sampledRows++;
        }

        // Columns with no data default to String
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (!hasData[columnIndex])
            {
                kinds[columnIndex] = DataKind.String;
            }
        }

        List<ColumnInfo> columns = new(headers.Length);
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            columns.Add(new ColumnInfo(headers[columnIndex].Trim(), kinds[columnIndex], nullable: true));
        }

        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char delimiter = GetDelimiter(descriptor);

        // First pass: infer schema to know column types
        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);

        using StreamReader reader = new(descriptor.FilePath);
        string? headerLine = await reader.ReadLineAsync(cancellationToken);
        if (headerLine is null)
        {
            yield break;
        }

        string[] headers = ParseCsvLine(headerLine, delimiter);

        // Build projection map: which columns to include
        int[] projectedIndices;
        string[] projectedNames;
        DataKind[] projectedKinds;

        if (requiredColumns is not null)
        {
            List<int> indices = new();
            List<string> names = new();
            List<DataKind> kinds = new();

            for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
            {
                string name = headers[columnIndex].Trim();
                if (requiredColumns.Contains(name))
                {
                    indices.Add(columnIndex);
                    names.Add(name);
                    kinds.Add(schema.Columns[columnIndex].Kind);
                }
            }

            projectedIndices = indices.ToArray();
            projectedNames = names.ToArray();
            projectedKinds = kinds.ToArray();
        }
        else
        {
            projectedIndices = new int[headers.Length];
            projectedNames = new string[headers.Length];
            projectedKinds = new DataKind[headers.Length];

            for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
            {
                projectedIndices[columnIndex] = columnIndex;
                projectedNames[columnIndex] = headers[columnIndex].Trim();
                projectedKinds[columnIndex] = schema.Columns[columnIndex].Kind;
            }
        }

        // Build name index once for the projected schema.
        Dictionary<string, int> nameIndex = new(projectedNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < projectedNames.Length; index++)
        {
            nameIndex[projectedNames[index]] = index;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? logicalLine = await ReadLogicalLineAsync(reader, cancellationToken);
            if (logicalLine is null)
            {
                break;
            }

            string[] fields = ParseCsvLine(logicalLine, delimiter);
            DataValue[] values = new DataValue[projectedIndices.Length];

            for (int projectionIndex = 0; projectionIndex < projectedIndices.Length; projectionIndex++)
            {
                int sourceIndex = projectedIndices[projectionIndex];
                string field = sourceIndex < fields.Length ? fields[sourceIndex].Trim() : string.Empty;

                values[projectionIndex] = ParseField(field, projectedKinds[projectionIndex]);
            }

            yield return new Row(projectedNames, values, nameIndex);
        }
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: null,
            EstimatedRowSizeBytes: null,
            SupportsSeek: true,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkByteRange>> MeasureChunkByteRangesAsync(
        TableDescriptor descriptor,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: MeasurementBufferSize, useAsync: true);

        byte[] buffer = new byte[MeasurementBufferSize];
        List<ChunkByteRange> ranges = new();

        long currentOffset = 0;
        long chunkStartOffset = 0;
        int rowsInChunk = 0;
        bool headerSkipped = false;
        bool inQuote = false;
        bool hasUnterminatedRow = false;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, MeasurementBufferSize), cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                byte b = buffer[i];

                if (b == (byte)'"')
                {
                    inQuote = !inQuote;
                    if (headerSkipped)
                    {
                        hasUnterminatedRow = true;
                    }
                }
                else if (b == (byte)'\n' && !inQuote)
                {
                    if (!headerSkipped)
                    {
                        headerSkipped = true;
                        chunkStartOffset = currentOffset + 1;
                    }
                    else
                    {
                        rowsInChunk++;
                        hasUnterminatedRow = false;

                        if (rowsInChunk >= chunkSize)
                        {
                            long chunkEndOffset = currentOffset + 1;
                            ranges.Add(new ChunkByteRange(
                                chunkStartOffset,
                                chunkEndOffset - chunkStartOffset,
                                rowsInChunk));
                            chunkStartOffset = chunkEndOffset;
                            rowsInChunk = 0;
                        }
                    }
                }
                else if (headerSkipped && b != (byte)'\r')
                {
                    hasUnterminatedRow = true;
                }

                currentOffset++;
            }
        }

        // Account for unterminated last row (file not ending with newline).
        if (hasUnterminatedRow)
        {
            rowsInChunk++;
        }

        if (rowsInChunk > 0)
        {
            ranges.Add(new ChunkByteRange(
                chunkStartOffset,
                currentOffset - chunkStartOffset,
                rowsInChunk));
        }

        return ranges;
    }

    /// <summary>
    /// Parses a single CSV field into a <see cref="DataValue"/> based on the inferred column kind.
    /// </summary>
    private static DataValue ParseField(string field, DataKind kind)
    {
        if (field.Length == 0)
        {
            return DataValue.Null(kind);
        }

        return kind switch
        {
            DataKind.Scalar when float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
                => DataValue.FromScalar(number),
            DataKind.Scalar => DataValue.Null(DataKind.Scalar),
            _ => DataValue.FromString(field)
        };
    }

    /// <summary>
    /// Gets the delimiter character from the descriptor options, defaulting to comma.
    /// </summary>
    private static char GetDelimiter(TableDescriptor descriptor)
    {
        if (descriptor.Options.TryGetValue("delimiter", out string? delimiterValue) &&
            delimiterValue.Length > 0)
        {
            return delimiterValue[0];
        }

        return DefaultDelimiter;
    }

    /// <summary>
    /// Thread-local reusable field buffer to avoid a <see cref="List{T}"/> allocation per CSV row.
    /// </summary>
    [ThreadStatic]
    private static List<string>? _fieldBuffer;

    /// <summary>
    /// Parses a CSV line into fields following RFC 4180 rules:
    /// - Fields may be quoted with double quotes
    /// - Quoted fields may contain delimiters, newlines, and escaped quotes ("")
    /// - Leading/trailing whitespace in unquoted fields is preserved
    /// </summary>
    private static string[] ParseCsvLine(string line, char delimiter)
    {
        List<string> fields = (_fieldBuffer ??= new(16));
        fields.Clear();
        int position = 0;

        while (position <= line.Length)
        {
            if (position == line.Length)
            {
                fields.Add(string.Empty);
                break;
            }

            if (line[position] == '"')
            {
                // Quoted field
                position++; // Skip opening quote
                int start = position;
                System.Text.StringBuilder builder = new();

                while (position < line.Length)
                {
                    if (line[position] == '"')
                    {
                        if (position + 1 < line.Length && line[position + 1] == '"')
                        {
                            // Escaped quote
                            builder.Append(line.AsSpan(start, position - start));
                            builder.Append('"');
                            position += 2;
                            start = position;
                        }
                        else
                        {
                            // End of quoted field
                            builder.Append(line.AsSpan(start, position - start));
                            position++; // Skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        position++;
                    }
                }

                // If we hit end of line without closing quote, take remaining content
                if (position <= line.Length && (position == line.Length || line[position - 1] != '"'))
                {
                    // Already handled by while loop break
                }

                fields.Add(builder.ToString());

                // Skip delimiter after quoted field
                if (position < line.Length && line[position] == delimiter)
                {
                    position++;
                }
            }
            else
            {
                // Unquoted field
                int nextDelimiter = line.IndexOf(delimiter, position);
                if (nextDelimiter == -1)
                {
                    fields.Add(line[position..]);
                    break;
                }
                else
                {
                    fields.Add(line[position..nextDelimiter]);
                    position = nextDelimiter + 1;
                }
            }
        }

        return fields.ToArray();
    }

    /// <summary>
    /// Thread-local reusable <see cref="System.Text.StringBuilder"/> for assembling
    /// logical CSV lines that span multiple physical lines (embedded newlines in quoted fields).
    /// </summary>
    [ThreadStatic]
    private static System.Text.StringBuilder? _lineBuilder;

    /// <summary>
    /// Reads a logical CSV line that may span multiple physical lines when
    /// quoted fields contain embedded newlines (RFC 4180).
    /// </summary>
    private static async Task<string?> ReadLogicalLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? firstLine = await reader.ReadLineAsync(cancellationToken);
        if (firstLine is null)
        {
            return null;
        }

        // Count unescaped quotes — if odd, the logical line continues
        int quoteCount = CountUnescapedQuotes(firstLine);
        if (quoteCount % 2 == 0)
        {
            return firstLine;
        }

        System.Text.StringBuilder builder = (_lineBuilder ??= new(1024));
        builder.Clear();
        builder.Append(firstLine);
        while (quoteCount % 2 != 0)
        {
            string? continuation = await reader.ReadLineAsync(cancellationToken);
            if (continuation is null)
            {
                break;
            }

            builder.Append('\n');
            builder.Append(continuation);
            quoteCount += CountUnescapedQuotes(continuation);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Counts quote characters in a line. Doubled quotes ("") count as two
    /// individual quotes, which keeps the parity correct for detecting
    /// whether we are inside a quoted field.
    /// </summary>
    private static int CountUnescapedQuotes(string line)
    {
        int count = 0;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                count++;
            }
        }
        return count;
    }
}
