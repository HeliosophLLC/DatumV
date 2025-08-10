using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads newline-delimited JSON (JSONL/NDJSON) files where each line is a
/// self-contained JSON object. Streams line-by-line for constant memory usage
/// regardless of file size. Type inference and value conversion are shared
/// with <see cref="JsonTableProvider"/> via <see cref="JsonTypeInference"/>.
/// </summary>
public sealed class JsonlTableProvider : IChunkMeasuringProvider
{
    /// <summary>Maximum number of lines sampled for schema inference.</summary>
    private const int SchemaSampleSize = 100;

    /// <summary>
    /// Byte-level scan buffer size for chunk measurement.
    /// </summary>
    private const int MeasurementBufferSize = 65536;

    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DataKind> columnKinds = new(StringComparer.OrdinalIgnoreCase);
        int sampled = 0;

        using StreamReader reader = new(descriptor.FilePath);

        while (sampled < SchemaSampleSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                DataKind detectedKind = JsonTypeInference.InferKind(property.Value);

                if (!columnKinds.TryGetValue(property.Name, out DataKind existingKind))
                {
                    columnKinds[property.Name] = detectedKind;
                }
                else
                {
                    columnKinds[property.Name] = JsonTypeInference.WidenKind(existingKind, detectedKind);
                }
            }

            sampled++;
        }

        List<ColumnInfo> columns = new(columnKinds.Count);
        foreach (KeyValuePair<string, DataKind> entry in columnKinds)
        {
            columns.Add(new ColumnInfo(entry.Key, entry.Value, nullable: true));
        }

        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Schema schema = await GetSchemaAsync(descriptor, cancellationToken);

        List<ColumnInfo> projectedColumns;
        if (requiredColumns is not null)
        {
            projectedColumns = new List<ColumnInfo>();
            foreach (ColumnInfo column in schema.Columns)
            {
                if (requiredColumns.Contains(column.Name))
                {
                    projectedColumns.Add(column);
                }
            }
        }
        else
        {
            projectedColumns = new List<ColumnInfo>(schema.Columns);
        }

        string[] names = new string[projectedColumns.Count];
        for (int index = 0; index < projectedColumns.Count; index++)
        {
            names[index] = projectedColumns[index].Name;
        }

        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Length; index++)
        {
            nameIndex[names[index]] = index;
        }

        using StreamReader reader = new(descriptor.FilePath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DataValue[] values = new DataValue[projectedColumns.Count];
            for (int columnIndex = 0; columnIndex < projectedColumns.Count; columnIndex++)
            {
                ColumnInfo column = projectedColumns[columnIndex];
                if (root.TryGetProperty(column.Name, out JsonElement propertyValue))
                {
                    values[columnIndex] = JsonTypeInference.ConvertElement(propertyValue, column.Kind);
                }
                else
                {
                    values[columnIndex] = DataValue.Null(column.Kind);
                }
            }

            yield return new Row(names, values, nameIndex);
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
                int lineStart = 0;
                bool lineHasNonWhitespace = false;
                bool lineStartsWithBrace = false;

                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];

                    if (b == (byte)'\n')
                    {
                        if (lineHasNonWhitespace && lineStartsWithBrace)
                        {
                            lineCount++;
                        }

                        lineStart = i + 1;
                        lineHasNonWhitespace = false;
                        lineStartsWithBrace = false;
                    }
                    else if (b != (byte)'\r' && b != (byte)' ' && b != (byte)'\t')
                    {
                        if (!lineHasNonWhitespace)
                        {
                            lineHasNonWhitespace = true;
                            lineStartsWithBrace = b == (byte)'{';
                        }
                    }
                }

                // Account for a final line not terminated by a newline.
                if (lineHasNonWhitespace && lineStartsWithBrace)
                {
                    lineCount++;
                }

                if (lineCount > 0)
                {
                    estimatedRowCount = fileSize * lineCount / bytesRead;
                    estimatedRowSizeBytes = fileSize / estimatedRowCount.Value;
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
        using FileStream stream = new(
            descriptor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: MeasurementBufferSize, useAsync: true);

        byte[] buffer = new byte[MeasurementBufferSize];
        List<ChunkByteRange> ranges = new();

        long currentOffset = 0;
        long chunkStartOffset = 0;
        int rowsInChunk = 0;
        bool lineHasNonWhitespace = false;
        bool lineStartsWithBrace = false;
        bool hasUnterminatedDataRow = false;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, MeasurementBufferSize), cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                byte b = buffer[i];

                if (b == (byte)'\n')
                {
                    if (lineHasNonWhitespace && lineStartsWithBrace)
                    {
                        rowsInChunk++;
                        hasUnterminatedDataRow = false;

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

                    lineHasNonWhitespace = false;
                    lineStartsWithBrace = false;
                }
                else if (b != (byte)'\r' && b != (byte)' ' && b != (byte)'\t')
                {
                    if (!lineHasNonWhitespace)
                    {
                        lineHasNonWhitespace = true;
                        lineStartsWithBrace = b == (byte)'{';
                        hasUnterminatedDataRow = lineStartsWithBrace;
                    }
                }

                currentOffset++;
            }
        }

        // Account for unterminated last line (file not ending with newline).
        if (hasUnterminatedDataRow)
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
}
