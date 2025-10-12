using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Deserializes CSV files into <see cref="RowBatch"/> streams. Uses
/// <see cref="SerializationContext.Arena"/> as the <see cref="IValueStore"/>
/// for string values — no ambient state needed.
/// </summary>
public sealed class CsvDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public CsvDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Detect delimiter from options/extension/content.
        char delimiter = DelimiterDetector.Detect(stream, _descriptor.Options, _descriptor.FilePath);

        // Detect header and infer types.
        bool? headerOverride = GetHeaderOverride(_descriptor.Options);
        HeaderDetectionResult detection = HeaderDetector.Detect(stream, delimiter, headerOverride);

        if (detection.ColumnNames.Length == 0)
            yield break;

        string[] names = detection.ColumnNames;
        DataKind[] kinds = detection.ColumnKinds;
        bool hasHeader = detection.HasHeader;

        // Build name index once (shared across all rows).
        Dictionary<string, int> nameIndex = new(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
            nameIndex[names[i]] = i;

        // Stream is at position 0 after detection. Create line reader.
        using LineReader lineReader = new(stream);

        // Skip header line if present.
        if (hasHeader)
            lineReader.ReadLineAsString();

        IValueStore store = context.Arena;
        RowBatch? batch = null;
        int lineNumber = hasHeader ? 1 : 0; // Header line already consumed.

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!lineReader.TryReadLogicalLine(out ReadOnlySpan<char> lineSpan))
                break;
            lineNumber++;

            DataValue[] values = context.Pool.RentDataValues(names.Length);

            if (!lineSpan.Contains('"'))
            {
                // Fast path: unquoted line, span-based parsing.
                int fieldStart = 0;
                int currentFieldIndex = 0;
                int columnIndex = 0;

                for (int charIndex = 0; charIndex <= lineSpan.Length && columnIndex < names.Length; charIndex++)
                {
                    if (charIndex == lineSpan.Length || lineSpan[charIndex] == delimiter)
                    {
                        if (currentFieldIndex == columnIndex)
                        {
                            ReadOnlySpan<char> fieldSpan = lineSpan[fieldStart..charIndex].Trim();
                            if (CsvParser.IsNullLiteral(fieldSpan))
                            {
                                values[columnIndex] = DataValue.Null(kinds[columnIndex]);
                            }
                            else if (kinds[columnIndex] == DataKind.String)
                            {
                                // String fields: store in Arena via IValueStore.
                                string fieldStr = fieldSpan.ToString();
                                values[columnIndex] = DataValue.FromString(fieldStr, store);
                            }
                            else
                            {
                                values[columnIndex] = CsvParser.ParseFieldSpan(fieldSpan, kinds[columnIndex]);
                            }

                            columnIndex++;
                        }

                        currentFieldIndex++;
                        fieldStart = charIndex + 1;
                    }
                }

                while (columnIndex < names.Length)
                {
                    values[columnIndex] = DataValue.Null(kinds[columnIndex]);
                    columnIndex++;
                }
            }
            else
            {
                // Slow path: quoted line, full RFC 4180 parsing.
                string logicalLine = lineSpan.ToString();
                List<string> fields = CsvParser.ParseCsvLineList(logicalLine, delimiter);

                for (int columnIndex = 0; columnIndex < names.Length; columnIndex++)
                {
                    string field = columnIndex < fields.Count ? fields[columnIndex].Trim() : string.Empty;
                    values[columnIndex] = CsvParser.ParseFieldString(field, kinds[columnIndex], store);
                }
            }

            batch ??= context.Pool.RentBatch(DefaultBatchSize);
            batch.Add(new Row(names, values, nameIndex));

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

    private static bool? GetHeaderOverride(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("header", out string? value))
        {
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return null;
    }
}
