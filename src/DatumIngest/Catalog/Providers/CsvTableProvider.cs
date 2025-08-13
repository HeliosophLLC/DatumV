using System.Globalization;
using System.Runtime.CompilerServices;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads CSV files conforming to RFC 4180. Supports configurable delimiter,
/// automatic numeric column detection, projection pushdown, byte-level
/// chunk measurement for index building, and automatic header row detection.
/// </summary>
/// <remarks>
/// <para>
/// When the <c>header</c> option is absent or set to <c>"auto"</c>, the provider
/// infers whether the first row is a header by comparing its type profile against
/// subsequent rows. If any column is predominantly numeric in rows 2–20 but the
/// corresponding row-1 value is non-numeric, the first row is treated as a header.
/// Otherwise it is treated as data and columns receive generated names
/// (<c>col_0</c>, <c>col_1</c>, …).
/// </para>
/// <para>
/// Set <c>header=true</c> to force the original behavior (first row is always a header)
/// or <c>header=false</c> to force generated column names and treat every row as data.
/// </para>
/// </remarks>
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
        bool? headerOverride = GetHeaderOverride(descriptor);

        using StreamReader reader = new(descriptor.FilePath);
        string? firstLine = await reader.ReadLineAsync(cancellationToken);
        if (firstLine is null)
        {
            throw new InvalidOperationException($"CSV file '{descriptor.FilePath}' is empty.");
        }

        string[] firstRowFields = ParseCsvLine(firstLine, delimiter);

        // Read up to 100 rows for type inference (also used for header detection).
        List<string[]> sampleRows = new();
        int sampledRows = 0;
        while (sampledRows < 100)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            sampleRows.Add(ParseCsvLine(line, delimiter));
            sampledRows++;
        }

        bool hasHeader = headerOverride ?? DetectHeader(firstRowFields, sampleRows, delimiter);

        string[] headers;
        List<string[]> dataRows;

        if (hasHeader)
        {
            headers = firstRowFields;
            dataRows = sampleRows;
        }
        else
        {
            headers = new string[firstRowFields.Length];
            for (int columnIndex = 0; columnIndex < firstRowFields.Length; columnIndex++)
            {
                headers[columnIndex] = $"col_{columnIndex}";
            }

            // Row 1 is data, prepend it.
            dataRows = new List<string[]>(sampleRows.Count + 1);
            dataRows.Add(firstRowFields);
            dataRows.AddRange(sampleRows);
        }

        // Infer column types from data rows.
        DataKind[] kinds = new DataKind[headers.Length];
        Array.Fill(kinds, DataKind.Scalar);
        bool[] hasData = new bool[headers.Length];

        foreach (string[] fields in dataRows)
        {
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
        }

        // Columns with no data default to String
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (!hasData[columnIndex])
            {
                kinds[columnIndex] = DataKind.String;
            }
        }

        // Second pass: detect ISO 8601 dates in String columns.
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (kinds[columnIndex] != DataKind.String || !hasData[columnIndex])
            {
                continue;
            }

            bool allDates = true;
            bool anyHasTime = false;

            foreach (string[] fields in dataRows)
            {
                if (columnIndex >= fields.Length)
                {
                    continue;
                }

                string field = fields[columnIndex].Trim();
                if (field.Length == 0)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    allDates = false;
                    break;
                }

                if (parsed.TimeOfDay != TimeSpan.Zero)
                {
                    anyHasTime = true;
                }
            }

            if (allDates)
            {
                kinds[columnIndex] = anyHasTime ? DataKind.DateTime : DataKind.Date;
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

        // First pass: infer schema to know column types and header status.
        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);
        bool hasHeader = HasHeaderRow(descriptor, schema);

        using StreamReader reader = new(descriptor.FilePath);
        string? firstLine = await reader.ReadLineAsync(cancellationToken);
        if (firstLine is null)
        {
            yield break;
        }

        string[] headerFields = ParseCsvLine(firstLine, delimiter);

        // Build projection map: which columns to include
        int[] projectedIndices;
        string[] projectedNames;
        DataKind[] projectedKinds;

        if (requiredColumns is not null)
        {
            List<int> indices = new();
            List<string> names = new();
            List<DataKind> kinds = new();

            for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
            {
                string name = schema.Columns[columnIndex].Name;
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
            projectedIndices = new int[schema.Columns.Count];
            projectedNames = new string[schema.Columns.Count];
            projectedKinds = new DataKind[schema.Columns.Count];

            for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
            {
                projectedIndices[columnIndex] = columnIndex;
                projectedNames[columnIndex] = schema.Columns[columnIndex].Name;
                projectedKinds[columnIndex] = schema.Columns[columnIndex].Kind;
            }
        }

        // Build name index once for the projected schema.
        Dictionary<string, int> nameIndex = new(projectedNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < projectedNames.Length; index++)
        {
            nameIndex[projectedNames[index]] = index;
        }

        // If headerless, the first line is data — emit it as a row.
        if (!hasHeader)
        {
            DataValue[] firstValues = new DataValue[projectedIndices.Length];
            for (int projectionIndex = 0; projectionIndex < projectedIndices.Length; projectionIndex++)
            {
                int sourceIndex = projectedIndices[projectionIndex];
                string field = sourceIndex < headerFields.Length ? headerFields[sourceIndex].Trim() : string.Empty;
                firstValues[projectionIndex] = ParseField(field, projectedKinds[projectionIndex]);
            }

            yield return new Row(projectedNames, firstValues, nameIndex);
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
    public async Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        long? estimatedRowCount = null;
        long? estimatedRowSizeBytes = null;

        if (!File.Exists(descriptor.FilePath))
        {
            return new ProviderCapabilities(
                EstimatedRowCount: null,
                EstimatedRowSizeBytes: null,
                SupportsSeek: true,
                ColumnCosts: new Dictionary<string, ColumnCost>());
        }

        FileInfo fileInfo = new(descriptor.FilePath);
        long fileSize = fileInfo.Length;

        if (fileSize > 0)
        {
            Schema schema = await GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
            bool hasHeader = HasHeaderRow(descriptor, schema);

            using FileStream stream = new(
                descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: MeasurementBufferSize, useAsync: true);

            byte[] buffer = new byte[MeasurementBufferSize];
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(0, MeasurementBufferSize), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead > 0)
            {
                int lineCount = 0;
                bool inQuote = false;

                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'"')
                    {
                        inQuote = !inQuote;
                    }
                    else if (b == (byte)'\n' && !inQuote)
                    {
                        lineCount++;
                    }
                }

                // Account for a final line not terminated by a newline.
                if (bytesRead < fileSize || buffer[bytesRead - 1] != (byte)'\n')
                {
                    lineCount++;
                }

                if (lineCount > 0)
                {
                    // Extrapolate total physical lines using the sample's bytes-per-line ratio,
                    // then subtract the header row if present.
                    long totalLines = fileSize * lineCount / bytesRead;
                    if (hasHeader && totalLines > 0)
                    {
                        totalLines--;
                    }

                    if (totalLines > 0)
                    {
                        estimatedRowCount = totalLines;
                        estimatedRowSizeBytes = fileSize / totalLines;
                    }
                }
            }
        }

        return new ProviderCapabilities(
            EstimatedRowCount: estimatedRowCount,
            EstimatedRowSizeBytes: estimatedRowSizeBytes,
            SupportsSeek: true,
            ColumnCosts: new Dictionary<string, ColumnCost>());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkByteRange>> MeasureChunkByteRangesAsync(
        TableDescriptor descriptor,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);
        bool hasHeader = HasHeaderRow(descriptor, schema);

        using FileStream stream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: MeasurementBufferSize, useAsync: true);

        byte[] buffer = new byte[MeasurementBufferSize];
        List<ChunkByteRange> ranges = new();

        long currentOffset = 0;
        long chunkStartOffset = 0;
        int rowsInChunk = 0;
        bool headerSkipped = !hasHeader; // If no header, treat first row as data immediately.
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
            DataKind.Date when DateOnly.TryParse(field, CultureInfo.InvariantCulture, out DateOnly date)
                => DataValue.FromDate(date),
            DataKind.Date when DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset dateTimeAsDate)
                => DataValue.FromDate(DateOnly.FromDateTime(dateTimeAsDate.DateTime)),
            DataKind.Date => DataValue.Null(DataKind.Date),
            DataKind.DateTime when DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset dateTime)
                => DataValue.FromDateTime(dateTime),
            DataKind.DateTime => DataValue.Null(DataKind.DateTime),
            _ => DataValue.FromString(field)
        };
    }

    /// <summary>
    /// Returns the explicit <c>header</c> option if set, or null for auto-detection.
    /// </summary>
    private static bool? GetHeaderOverride(TableDescriptor descriptor)
    {
        if (descriptor.Options.TryGetValue("header", out string? headerValue))
        {
            if (headerValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (headerValue.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether the file has a header row by re-examining the schema.
    /// When columns use generated names (<c>col_0</c>, <c>col_1</c>, …), the file
    /// is headerless and the first physical row is data.
    /// </summary>
    private static bool HasHeaderRow(TableDescriptor descriptor, Schema schema)
    {
        bool? headerOverride = GetHeaderOverride(descriptor);
        if (headerOverride.HasValue)
        {
            return headerOverride.Value;
        }

        // Generated names follow the pattern col_N — if all columns match, no header.
        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            if (schema.Columns[columnIndex].Name != $"col_{columnIndex}")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Infers whether the first row of a CSV file is a header by comparing its
    /// type profile against subsequent data rows.
    /// </summary>
    /// <remarks>
    /// The heuristic: if any column is predominantly numeric in <paramref name="dataRows"/>
    /// (rows 2–N) but the corresponding <paramref name="firstRowFields"/> value is
    /// non-numeric, the first row is treated as a header. This catches the common case
    /// of <c>age,income</c> followed by <c>39,77516</c>.
    /// When all columns have matching type profiles between row 1 and subsequent rows
    /// (e.g. all-numeric or all-string), the first row is treated as data and columns
    /// receive generated names.
    /// </remarks>
    private static bool DetectHeader(string[] firstRowFields, List<string[]> dataRows, char delimiter)
    {
        if (dataRows.Count == 0)
        {
            // Only one row in the file — can't compare, assume header for backward compatibility.
            return true;
        }

        int columnCount = firstRowFields.Length;

        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string firstValue = firstRowFields[columnIndex].Trim();
            bool firstIsNumeric = firstValue.Length > 0 &&
                float.TryParse(firstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            // Count how many data rows have a numeric value in this column.
            int numericCount = 0;
            int nonEmptyCount = 0;

            foreach (string[] row in dataRows)
            {
                if (columnIndex >= row.Length)
                {
                    continue;
                }

                string field = row[columnIndex].Trim();
                if (field.Length == 0)
                {
                    continue;
                }

                nonEmptyCount++;
                if (float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    numericCount++;
                }
            }

            // Column is predominantly numeric if > 50% of non-empty data values parse as numbers.
            bool columnIsNumeric = nonEmptyCount > 0 && (double)numericCount / nonEmptyCount > 0.5;

            // If data is numeric but row 1 is not numeric → row 1 is a header.
            if (columnIsNumeric && !firstIsNumeric)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the delimiter character from the descriptor options, falling back to
    /// extension-based heuristics (<c>.tsv</c> → tab) and then content sniffing.
    /// </summary>
    private static char GetDelimiter(TableDescriptor descriptor)
    {
        if (descriptor.Options.TryGetValue("delimiter", out string? delimiterValue) &&
            delimiterValue.Length > 0)
        {
            return delimiterValue[0];
        }

        // .tsv files always use tab.
        string extension = Path.GetExtension(descriptor.FilePath);
        if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return '\t';
        }

        // Sniff delimiter from file content when the file exists.
        if (File.Exists(descriptor.FilePath))
        {
            return CsvDelimiterDetector.Detect(descriptor.FilePath);
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
