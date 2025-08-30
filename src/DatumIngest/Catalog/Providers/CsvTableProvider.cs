using System.Globalization;
using System.Runtime.CompilerServices;
using DatumIngest.Execution;
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
/// infers whether the first row is a header using two heuristics applied in order:
/// (1) if any column is predominantly numeric in subsequent rows but the first-row
/// value is non-numeric, the first row is a header; (2) for all-string files, if
/// every first-row value is absent from all sampled data rows for its column, the
/// first row is a header. When neither heuristic fires, the first row is treated as
/// data and columns receive generated names (<c>col_0</c>, <c>col_1</c>, …).
/// </para>
/// <para>
/// Set <c>header=true</c> to force the original behavior (first row is always a header)
/// or <c>header=false</c> to force generated column names and treat every row as data.
/// </para>
/// </remarks>
public sealed class CsvTableProvider : IChunkMeasuringProvider, IPartitionedTableProvider
{
    private const char DefaultDelimiter = ',';

    /// <summary>
    /// Byte-level scan buffer size for chunk measurement.
    /// </summary>
    private const int MeasurementBufferSize = 65536;

    /// <summary>
    /// Buffer size for the <see cref="StreamReader"/> used by <see cref="OpenAsync"/>.
    /// The .NET default (1,024 bytes) causes excessive buffer refills for large files;
    /// 64 KiB keeps one I/O read worth ~3,800 lines of a typical numeric CSV.
    /// </summary>
    private const int ReaderBufferSize = 65536;

    /// <summary>
    /// Number of rows per <see cref="RowBatch"/> emitted by streaming read methods.
    /// </summary>
    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        char delimiter = GetDelimiter(descriptor);
        bool? headerOverride = GetHeaderOverride(descriptor);

        using StreamReader reader = new(CompressionStreamFactory.OpenRead(descriptor));
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

            // Strip trailing empty headers produced by a trailing delimiter on the header line.
            int usableColumns = headers.Length;
            while (usableColumns > 0 && headers[usableColumns - 1].Trim().Length == 0)
            {
                usableColumns--;
            }

            if (usableColumns < headers.Length)
            {
                headers = headers[..usableColumns];
            }
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
        // Numeric detection: try long (integer) first, then double (float).
        // Per-column tracking: whether any value failed integer parse, whether any value
        // has a fractional part, and integer min/max for narrowing.
        DataKind[] kinds = new DataKind[headers.Length];
        bool[] hasData = new bool[headers.Length];
        bool[] numericFailed = new bool[headers.Length];
        bool[] integerFailed = new bool[headers.Length];
        bool[] hasFractionalValues = new bool[headers.Length];
        long[] integerMinimum = new long[headers.Length];
        long[] integerMaximum = new long[headers.Length];
        Array.Fill(integerMinimum, long.MaxValue);
        Array.Fill(integerMaximum, long.MinValue);

        foreach (string[] fields in dataRows)
        {
            for (int columnIndex = 0; columnIndex < Math.Min(fields.Length, headers.Length); columnIndex++)
            {
                string field = fields[columnIndex].Trim();
                if (field.Length == 0 || numericFailed[columnIndex])
                {
                    continue;
                }

                hasData[columnIndex] = true;

                if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
                {
                    if (longValue < integerMinimum[columnIndex])
                    {
                        integerMinimum[columnIndex] = longValue;
                    }

                    if (longValue > integerMaximum[columnIndex])
                    {
                        integerMaximum[columnIndex] = longValue;
                    }

                    continue;
                }

                if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
                {
                    integerFailed[columnIndex] = true;
                    if (doubleValue != Math.Floor(doubleValue) || double.IsInfinity(doubleValue))
                    {
                        hasFractionalValues[columnIndex] = true;
                    }

                    continue;
                }

                numericFailed[columnIndex] = true;
            }
        }

        // Determine numeric types from observation state.
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (!hasData[columnIndex])
            {
                kinds[columnIndex] = DataKind.String;
            }
            else if (numericFailed[columnIndex])
            {
                kinds[columnIndex] = DataKind.String;
            }
            else if (!integerFailed[columnIndex])
            {
                // Pure integer column with only 0 and 1 values → Boolean.
                if (integerMinimum[columnIndex] >= 0 && integerMaximum[columnIndex] <= 1)
                {
                    kinds[columnIndex] = DataKind.Boolean;
                }
                else
                {
                    // Select the narrowest signed kind that covers the range.
                    kinds[columnIndex] = NarrowestIntegerKind(integerMinimum[columnIndex], integerMaximum[columnIndex]);
                }
            }
            else if (!hasFractionalValues[columnIndex])
            {
                // Float-encoded integer (e.g. pandas NaN-poisoned columns like CGID=16929.0).
                kinds[columnIndex] = DataKind.Int64;
            }
            else
            {
                kinds[columnIndex] = DataKind.Float64;
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

        // Third pass: detect UUIDs (hyphenated and dashless) in String columns.
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (kinds[columnIndex] != DataKind.String || !hasData[columnIndex])
            {
                continue;
            }

            bool allUuids = true;

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

                if (!Guid.TryParseExact(field, "D", out _))
                {
                    allUuids = false;
                    break;
                }
            }

            if (allUuids)
            {
                kinds[columnIndex] = DataKind.Uuid;
            }
        }

        // Fourth pass: detect boolean text ("true"/"false") in String columns.
        for (int columnIndex = 0; columnIndex < headers.Length; columnIndex++)
        {
            if (kinds[columnIndex] != DataKind.String || !hasData[columnIndex])
            {
                continue;
            }

            bool allBoolean = true;

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

                if (!field.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                    !field.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    allBoolean = false;
                    break;
                }
            }

            if (allBoolean)
            {
                kinds[columnIndex] = DataKind.Boolean;
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
    public async IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char delimiter = GetDelimiter(descriptor);

        // First pass: infer schema to know column types and header status.
        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);
        bool hasHeader = HasHeaderRow(descriptor, schema);

        using StreamReader reader = new(
            CompressionStreamFactory.OpenRead(descriptor),
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: ReaderBufferSize);
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

        RowBatch? batch = null;

        // If headerless, the first line is data — emit it as a row.
        if (!hasHeader)
        {
            Row firstRow = GlobalBufferPool.RentRow(projectedIndices.Length);
            firstRow.UpdateSchema(projectedNames, nameIndex);
            DataValue[] firstValues = firstRow.RawValues;
            for (int projectionIndex = 0; projectionIndex < projectedIndices.Length; projectionIndex++)
            {
                int sourceIndex = projectedIndices[projectionIndex];
                string field = sourceIndex < headerFields.Length ? headerFields[sourceIndex].Trim() : string.Empty;
                firstValues[projectionIndex] = ParseField(field, projectedKinds[projectionIndex]);
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(firstRow);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? logicalLine = await ReadLogicalLineAsync(reader, cancellationToken);
            if (logicalLine is null)
            {
                break;
            }

            Row row = GlobalBufferPool.RentRow(projectedIndices.Length);
            row.UpdateSchema(projectedNames, nameIndex);
            DataValue[] values = row.RawValues;

            // Fast path for unquoted lines: extract fields as spans and parse scalars
            // directly, avoiding per-field substring allocations. This is the common case
            // for numeric CSVs (e.g. Instacart order_products__prior).
            if (!logicalLine.Contains('"'))
            {
                ReadOnlySpan<char> lineSpan = logicalLine.AsSpan();
                int fieldStart = 0;
                int currentFieldIndex = 0;
                int projectionIndex = 0;

                for (int charIndex = 0; charIndex <= lineSpan.Length && projectionIndex < projectedIndices.Length; charIndex++)
                {
                    if (charIndex == lineSpan.Length || lineSpan[charIndex] == delimiter)
                    {
                        if (currentFieldIndex == projectedIndices[projectionIndex])
                        {
                            ReadOnlySpan<char> fieldSpan = lineSpan[fieldStart..charIndex].Trim();
                            values[projectionIndex] = ParseFieldSpan(fieldSpan, projectedKinds[projectionIndex]);
                            projectionIndex++;
                        }

                        currentFieldIndex++;
                        fieldStart = charIndex + 1;
                    }
                }

                while (projectionIndex < projectedIndices.Length)
                {
                    values[projectionIndex] = DataValue.Null(projectedKinds[projectionIndex]);
                    projectionIndex++;
                }
            }
            else
            {
                // Quoted line: fall back to full RFC 4180 parsing.
                List<string> fields = ParseCsvLineList(logicalLine, delimiter);

                for (int projectionIndex = 0; projectionIndex < projectedIndices.Length; projectionIndex++)
                {
                    int sourceIndex = projectedIndices[projectionIndex];
                    string field = sourceIndex < fields.Count ? fields[sourceIndex].Trim() : string.Empty;

                    values[projectionIndex] = ParseField(field, projectedKinds[projectionIndex]);
                }
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(row);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
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

        // Compressed files do not support byte-level seeking or estimation.
        if (descriptor.Compression != CompressionKind.None)
        {
            return new ProviderCapabilities(
                EstimatedRowCount: null,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
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
            DataKind.Float32 when float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatNumber)
                => DataValue.FromFloat32(floatNumber),
            DataKind.Float32 => DataValue.Null(DataKind.Float32),
            DataKind.Float64 when double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleNumber)
                => DataValue.FromFloat64(doubleNumber),
            DataKind.Float64 => DataValue.Null(DataKind.Float64),
            DataKind.Int8 when sbyte.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte int8Value)
                => DataValue.FromInt8(int8Value),
            DataKind.Int8 => DataValue.Null(DataKind.Int8),
            DataKind.Int16 when short.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out short int16Value)
                => DataValue.FromInt16(int16Value),
            DataKind.Int16 => DataValue.Null(DataKind.Int16),
            DataKind.Int32 when int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int int32Value)
                => DataValue.FromInt32(int32Value),
            DataKind.Int32 => DataValue.Null(DataKind.Int32),
            DataKind.Int64 when long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long int64Value)
                => DataValue.FromInt64(int64Value),
            // Float-encoded integer: parse as double, truncate to long (handles "16929.0" etc.)
            DataKind.Int64 when double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double floatEncodedValue)
                => DataValue.FromInt64((long)floatEncodedValue),
            DataKind.Int64 => DataValue.Null(DataKind.Int64),
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
            DataKind.Uuid when Guid.TryParse(field, out Guid uuid)
                => DataValue.FromUuid(uuid),
            DataKind.Uuid => DataValue.Null(DataKind.Uuid),
            DataKind.Boolean when field is "1" || field.Equals("true", StringComparison.OrdinalIgnoreCase)
                => DataValue.FromBoolean(true),
            DataKind.Boolean when field is "0" || field.Equals("false", StringComparison.OrdinalIgnoreCase)
                => DataValue.FromBoolean(false),
            DataKind.Boolean => DataValue.Null(DataKind.Boolean),
            _ => DataValue.FromString(field)
        };
    }

    /// <summary>
    /// Span-based overload that parses numeric fields directly from a <see cref="ReadOnlySpan{T}"/>
    /// without allocating a substring. Falls back to the string-based overload for non-numeric types
    /// that require materialized strings (dates, UUIDs, strings).
    /// </summary>
    private static DataValue ParseFieldSpan(ReadOnlySpan<char> field, DataKind kind)
    {
        if (field.IsEmpty)
        {
            return DataValue.Null(kind);
        }

        switch (kind)
        {
            case DataKind.Float32:
                return float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatNumber)
                    ? DataValue.FromFloat32(floatNumber)
                    : DataValue.Null(DataKind.Float32);
            case DataKind.Float64:
                return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleNumber)
                    ? DataValue.FromFloat64(doubleNumber)
                    : DataValue.Null(DataKind.Float64);
            case DataKind.Int8:
                return sbyte.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte int8Value)
                    ? DataValue.FromInt8(int8Value)
                    : DataValue.Null(DataKind.Int8);
            case DataKind.Int16:
                return short.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out short int16Value)
                    ? DataValue.FromInt16(int16Value)
                    : DataValue.Null(DataKind.Int16);
            case DataKind.Int32:
                return int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int int32Value)
                    ? DataValue.FromInt32(int32Value)
                    : DataValue.Null(DataKind.Int32);
            case DataKind.Int64:
                if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long int64Value))
                {
                    return DataValue.FromInt64(int64Value);
                }

                // Float-encoded integer: parse as double, truncate to long.
                return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double floatEncodedValue)
                    ? DataValue.FromInt64((long)floatEncodedValue)
                    : DataValue.Null(DataKind.Int64);
            default:
                return ParseField(field.ToString(), kind);
        }
    }

    /// <summary>
    /// Selects the narrowest signed integer <see cref="DataKind"/> whose range covers
    /// both <paramref name="minimum"/> and <paramref name="maximum"/>.
    /// </summary>
    /// <remarks>
    /// Floors at <see cref="DataKind.Int32"/> because CSV type inference is based on a
    /// small sample of rows. Choosing Int8 or Int16 from a sample causes silent data loss
    /// when later rows contain values outside the narrower range — the parse fails and the
    /// value becomes null. Int8/Int16 remain valid for typed sources (Parquet, HDF5, .datum)
    /// where the schema is authoritative.
    /// </remarks>
    private static DataKind NarrowestIntegerKind(long minimum, long maximum)
    {
        if (minimum >= int.MinValue && maximum <= int.MaxValue)
        {
            return DataKind.Int32;
        }

        return DataKind.Int64;
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
    /// <para>
    /// Two heuristics are applied in order:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>Numeric mismatch.</b> If any column is predominantly numeric in
    /// <paramref name="dataRows"/> (rows 2–N) but the corresponding
    /// <paramref name="firstRowFields"/> value is non-numeric, the first row is
    /// a header. This catches <c>age,income</c> followed by <c>39,77516</c>.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Value disjointness.</b> For all-string files (no numeric mismatch),
    /// if every non-empty first-row value is absent from all sampled data rows
    /// for its column and at least two data rows are available, the first row is
    /// a header. This catches <c>product_category_name,product_category_name_english</c>
    /// followed by <c>beleza_saude,health_beauty</c>.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    private static bool DetectHeader(string[] firstRowFields, List<string[]> dataRows, char delimiter)
    {
        if (dataRows.Count == 0)
        {
            // Only one row in the file — can't compare, assume header for backward compatibility.
            return true;
        }

        int columnCount = firstRowFields.Length;

        // --- Pass 1: numeric mismatch ---
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string firstValue = firstRowFields[columnIndex].Trim();
            bool firstIsNumeric = firstValue.Length > 0 &&
                double.TryParse(firstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

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
                if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
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

        // --- Pass 2: value disjointness (for all-string files) ---
        // If every first-row value is absent from all data rows for that column,
        // the first row is likely a header row rather than data.
        if (dataRows.Count >= 2)
        {
            bool allDisjoint = true;
            int nonEmptyFirstRowValues = 0;

            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                string firstValue = firstRowFields[columnIndex].Trim();
                if (firstValue.Length == 0)
                {
                    continue;
                }

                nonEmptyFirstRowValues++;

                foreach (string[] row in dataRows)
                {
                    if (columnIndex >= row.Length)
                    {
                        continue;
                    }

                    if (row[columnIndex].Trim().Equals(firstValue, StringComparison.OrdinalIgnoreCase))
                    {
                        allDisjoint = false;
                        break;
                    }
                }

                if (!allDisjoint)
                {
                    break;
                }
            }

            if (allDisjoint && nonEmptyFirstRowValues > 0)
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

        // .tsv files always use tab. Strip compression extensions first
        // so that "data.tsv.gz" is recognised as tab-separated.
        string fileName = Path.GetFileName(descriptor.FilePath);
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            extension = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));
        }

        if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return '\t';
        }

        // Sniff delimiter from file content when the file exists.
        if (File.Exists(descriptor.FilePath))
        {
            if (descriptor.Compression != CompressionKind.None)
            {
                using Stream stream = CompressionStreamFactory.OpenRead(descriptor);
                return CsvDelimiterDetector.Detect(stream);
            }

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
        return ParseCsvLineList(line, delimiter).ToArray();
    }

    /// <summary>
    /// Parses a CSV line into fields, returning the thread-local field list directly
    /// to avoid a per-call array allocation. Callers that store the result must call
    /// <c>.ToArray()</c>; callers that only index into the result can use it as-is.
    /// The returned list is only valid until the next call to this method on the same thread.
    /// </summary>
    private static List<string> ParseCsvLineList(string line, char delimiter)
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
                else if (position >= line.Length)
                {
                    // End of line after closing quote — no more fields to parse.
                    break;
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

        return fields;
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

        // Lines without quotes (the vast majority of numeric CSVs) can be returned
        // immediately. Contains uses SIMD-vectorised search in .NET 8+.
        if (!firstLine.Contains('"'))
        {
            return firstLine;
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

    // -------------------------------------------------------------------------
    // IPartitionedTableProvider — parallel multi-stream scan
    // -------------------------------------------------------------------------

    /// <summary>Minimum byte size of each partition. Files smaller than this are not split.</summary>
    private const long MinPartitionBytes = 256 * 1024;

    /// <inheritdoc />
    public async Task<IReadOnlyList<IAsyncEnumerable<RowBatch>>?> OpenPartitionsAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        int maxPartitions,
        CancellationToken cancellationToken)
    {
        // Compressed files are not seekable — fall back to single-stream.
        if (descriptor.Compression != CompressionKind.None
            || !File.Exists(descriptor.FilePath))
        {
            return null;
        }

        FileInfo fileInfo = new(descriptor.FilePath);
        long fileSize = fileInfo.Length;

        if (fileSize == 0)
        {
            return null;
        }

        Schema schema = await GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
        bool hasHeader = HasHeaderRow(descriptor, schema);
        char delimiter = GetDelimiter(descriptor);

        // Build projection metadata shared across all partitions.
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

        Dictionary<string, int> nameIndex = new(projectedNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < projectedNames.Length; index++)
        {
            nameIndex[projectedNames[index]] = index;
        }

        // Locate the first byte of data (end of the header line).
        long dataStartOffset = await FindDataStartOffsetAsync(
            descriptor.FilePath, hasHeader, cancellationToken).ConfigureAwait(false);

        long dataSize = fileSize - dataStartOffset;

        // Cap partitions so each one covers at least MinPartitionBytes.
        int workablePartitions = (int)Math.Min(
            maxPartitions,
            Math.Max(1, dataSize / MinPartitionBytes));

        if (workablePartitions <= 1)
        {
            return null;
        }

        // Compute N aligned partition start offsets.
        long[] partitionStarts = new long[workablePartitions];
        long[] partitionEnds = new long[workablePartitions];

        partitionStarts[0] = dataStartOffset;

        for (int partitionIndex = 1; partitionIndex < workablePartitions; partitionIndex++)
        {
            long rawOffset = dataStartOffset + (long)partitionIndex * (dataSize / workablePartitions);
            long alignedOffset = await FindLineStartAsync(
                descriptor.FilePath, rawOffset, cancellationToken).ConfigureAwait(false);

            if (alignedOffset < 0 || alignedOffset >= fileSize)
            {
                // Could not align — truncate to the partitions found so far.
                workablePartitions = partitionIndex;
                break;
            }

            partitionStarts[partitionIndex] = alignedOffset;
            partitionEnds[partitionIndex - 1] = alignedOffset;
        }

        partitionEnds[workablePartitions - 1] = fileSize;

        if (workablePartitions <= 1)
        {
            return null;
        }

        List<IAsyncEnumerable<RowBatch>> partitions = new(workablePartitions);

        for (int partitionIndex = 0; partitionIndex < workablePartitions; partitionIndex++)
        {
            partitions.Add(OpenPartitionCoreAsync(
                descriptor.FilePath,
                projectedNames, projectedIndices, projectedKinds, nameIndex,
                delimiter,
                partitionStarts[partitionIndex],
                partitionEnds[partitionIndex],
                cancellationToken));
        }

        return partitions;
    }

    /// <summary>
    /// Returns the byte offset of the first data row — the position immediately
    /// after the header line (or 0 when there is no header).
    /// </summary>
    private static async Task<long> FindDataStartOffsetAsync(
        string filePath, bool hasHeader, CancellationToken cancellationToken)
    {
        if (!hasHeader)
        {
            return 0;
        }

        using FileStream stream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 512);

        byte[] buffer = new byte[512];
        long bytesScanned = 0;

        while (true)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                // Header occupies entire file — no data rows.
                return stream.Position;
            }

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    return bytesScanned + i + 1;
                }
            }

            bytesScanned += bytesRead;
        }
    }

    /// <summary>
    /// Returns the byte offset of the first character of the line that begins
    /// at or after <paramref name="approximateOffset"/>. Returns <c>-1</c> when
    /// EOF is reached before any newline is found.
    /// </summary>
    private static async Task<long> FindLineStartAsync(
        string filePath, long approximateOffset, CancellationToken cancellationToken)
    {
        // Seek one byte before the target so that a '\n' landing exactly at
        // approximateOffset is treated as the end of the preceding line.
        long searchFrom = Math.Max(0, approximateOffset - 1);

        using FileStream stream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 512);

        stream.Seek(searchFrom, SeekOrigin.Begin);

        byte[] buffer = new byte[512];
        long currentOffset = searchFrom;

        while (true)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return -1; // EOF reached without finding a newline.
            }

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    return currentOffset + i + 1;
                }
            }

            currentOffset += bytesRead;
        }
    }

    /// <summary>
    /// Reads rows from a byte range of a CSV file. The range must start at an
    /// aligned line boundary (first byte of a data row) and end at a line boundary
    /// (the byte after the terminating '\n'). Schema metadata is provided by the
    /// caller and shared across all concurrent partitions.
    /// </summary>
    private static async IAsyncEnumerable<RowBatch> OpenPartitionCoreAsync(
        string filePath,
        string[] projectedNames,
        int[] projectedIndices,
        DataKind[] projectedKinds,
        Dictionary<string, int> nameIndex,
        char delimiter,
        long partitionStart,
        long partitionEnd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long partitionLength = partitionEnd - partitionStart;
        if (partitionLength <= 0)
        {
            yield break;
        }

        // leaveOpen: true so the StreamReader does not close the FileStream —
        // the using block below is the sole owner of the FileStream lifetime.
        using FileStream fileStream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: ReaderBufferSize, useAsync: true);

        fileStream.Seek(partitionStart, SeekOrigin.Begin);

        BoundedStream bounded = new(fileStream, partitionLength);

        using StreamReader reader = new(
            bounded,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: ReaderBufferSize,
            leaveOpen: true);

        RowBatch? batch = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? logicalLine = await ReadLogicalLineAsync(reader, cancellationToken)
                .ConfigureAwait(false);

            if (logicalLine is null)
            {
                break;
            }

            Row row = GlobalBufferPool.RentRow(projectedIndices.Length);
            row.UpdateSchema(projectedNames, nameIndex);
            DataValue[] values = row.RawValues;

            if (!logicalLine.Contains('"'))
            {
                ReadOnlySpan<char> lineSpan = logicalLine.AsSpan();
                int fieldStart = 0;
                int currentFieldIndex = 0;
                int projectionIndex = 0;

                for (int charIndex = 0; charIndex <= lineSpan.Length && projectionIndex < projectedIndices.Length; charIndex++)
                {
                    if (charIndex == lineSpan.Length || lineSpan[charIndex] == delimiter)
                    {
                        if (currentFieldIndex == projectedIndices[projectionIndex])
                        {
                            ReadOnlySpan<char> fieldSpan = lineSpan[fieldStart..charIndex].Trim();
                            values[projectionIndex] = ParseFieldSpan(fieldSpan, projectedKinds[projectionIndex]);
                            projectionIndex++;
                        }

                        currentFieldIndex++;
                        fieldStart = charIndex + 1;
                    }
                }

                while (projectionIndex < projectedIndices.Length)
                {
                    values[projectionIndex] = DataValue.Null(projectedKinds[projectionIndex]);
                    projectionIndex++;
                }
            }
            else
            {
                List<string> fields = ParseCsvLineList(logicalLine, delimiter);

                for (int projectionIndex = 0; projectionIndex < projectedIndices.Length; projectionIndex++)
                {
                    int sourceIndex = projectedIndices[projectionIndex];
                    string field = sourceIndex < fields.Count ? fields[sourceIndex].Trim() : string.Empty;
                    values[projectionIndex] = ParseField(field, projectedKinds[projectionIndex]);
                }
            }

            batch ??= RowBatch.Rent(DefaultBatchSize);
            batch.Add(row);
            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Limits reads from an inner stream to a fixed byte budget. When the
    /// budget is exhausted, all subsequent reads return zero bytes (the
    /// end-of-stream signal that stops a wrapping <see cref="StreamReader"/>).
    /// </summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        /// <summary>Wraps <paramref name="inner"/> and allows reading at most <paramref name="limit"/> bytes.</summary>
        internal BoundedStream(Stream inner, long limit)
        {
            _inner = inner;
            _remaining = limit;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, _remaining);
            int bytesRead = _inner.Read(buffer, offset, toRead);
            _remaining -= bytesRead;
            return bytesRead;
        }

        /// <inheritdoc />
        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, _remaining);
            int bytesRead = await _inner.ReadAsync(
                buffer.AsMemory(offset, toRead), cancellationToken).ConfigureAwait(false);
            _remaining -= bytesRead;
            return bytesRead;
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, _remaining);
            int bytesRead = await _inner.ReadAsync(
                buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _remaining -= bytesRead;
            return bytesRead;
        }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
