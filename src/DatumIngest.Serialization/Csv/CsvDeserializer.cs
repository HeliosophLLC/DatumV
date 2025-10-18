using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Deserializes CSV files into <see cref="RowBatch"/> streams.
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

        // Adaptive per-column cache for date/time formats. Populated on the first
        // successful parse of each Date/DateTime column; subsequent rows use
        // TryParseExact with the cached format, avoiding the BCL's flexible parser.
        TemporalFormatCache temporalCache = new(names.Length);

        // Stream is at position 0 after detection. Create line reader.
        using LineReader lineReader = new(stream);

        // Skip header line if present.
        if (hasHeader)
            lineReader.ReadLineAsString();

        RowBatch? batch = null;
        int lineNumber = hasHeader ? 1 : 0; // Header line already consumed.

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!lineReader.TryReadLogicalLine(out ReadOnlySpan<char> lineSpan))
                break;
            lineNumber++;

            DataValue[] values = context.Pool.RentDataValues(names.Length);
            batch ??= context.Pool.RentRowBatch(DefaultBatchSize);

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
                            values[columnIndex] = ParseField(fieldSpan, kinds[columnIndex], columnIndex, temporalCache, batch.Arena);
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
                // Slow path: quoted line, full RFC 4180 parsing — span-based so the
                // common case (fully-quoted fields without embedded `""`) stays
                // zero-allocation. A thread-static char buffer is used only when a
                // field contains an escaped quote and must be unescaped.
                ParseQuotedLineIntoValues(lineSpan, delimiter, kinds, names, values, batch.Arena, temporalCache);
            }

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

    /// <summary>
    /// Thread-static scratch buffer used only when a quoted field contains an escaped
    /// <c>""</c> sequence and must be unescaped. Grows as needed; the backing array
    /// outlives the thread.
    /// </summary>
    [ThreadStatic]
    private static char[]? _unescapeBuffer;

    /// <summary>
    /// Parses a CSV line containing at least one quote character, writing one
    /// <see cref="DataValue"/> per column into <paramref name="values"/>. Fields without
    /// embedded <c>""</c> resolve to a slice of <paramref name="line"/>; fields that
    /// require unescaping are materialised into a thread-static char buffer. No managed
    /// <see cref="string"/> is allocated for any field.
    /// </summary>
    private static void ParseQuotedLineIntoValues(
        ReadOnlySpan<char> line,
        char delimiter,
        DataKind[] kinds,
        string[] names,
        DataValue[] values,
        IValueStore store,
        TemporalFormatCache temporalCache)
    {
        int position = 0;
        int columnIndex = 0;

        while (columnIndex < names.Length && position <= line.Length)
        {
            ReadOnlySpan<char> fieldSpan;

            if (position < line.Length && line[position] == '"')
            {
                // Quoted field. Scan for the closing quote; if we hit `""` in the middle
                // we have to materialise the unescaped content into the scratch buffer.
                position++;
                int segmentStart = position;
                int unescapeLength = 0;
                bool needsUnescape = false;

                while (position < line.Length)
                {
                    if (line[position] != '"')
                    {
                        position++;
                        continue;
                    }

                    // Saw a quote: either an escape (`""`) or the closing quote.
                    if (position + 1 < line.Length && line[position + 1] == '"')
                    {
                        if (!needsUnescape)
                        {
                            char[] buffer = EnsureUnescapeBuffer(line.Length);
                            line.Slice(segmentStart, position - segmentStart).CopyTo(buffer);
                            unescapeLength = position - segmentStart;
                            needsUnescape = true;
                        }
                        else
                        {
                            line.Slice(segmentStart, position - segmentStart).CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                            unescapeLength += position - segmentStart;
                        }
                        _unescapeBuffer![unescapeLength++] = '"';
                        position += 2;
                        segmentStart = position;
                    }
                    else
                    {
                        // Closing quote.
                        if (needsUnescape)
                        {
                            line.Slice(segmentStart, position - segmentStart).CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                            unescapeLength += position - segmentStart;
                            fieldSpan = _unescapeBuffer.AsSpan(0, unescapeLength);
                        }
                        else
                        {
                            fieldSpan = line.Slice(segmentStart, position - segmentStart);
                        }
                        position++;
                        if (position < line.Length && line[position] == delimiter) position++;
                        goto ProcessField;
                    }
                }

                // Unterminated quote — take whatever we have.
                if (needsUnescape)
                {
                    line.Slice(segmentStart, position - segmentStart).CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                    unescapeLength += position - segmentStart;
                    fieldSpan = _unescapeBuffer.AsSpan(0, unescapeLength);
                }
                else
                {
                    fieldSpan = line.Slice(segmentStart, position - segmentStart);
                }
            }
            else
            {
                // Unquoted field — read up to the next delimiter.
                int remaining = line.Length - position;
                int nextDelim = remaining > 0 ? line.Slice(position).IndexOf(delimiter) : -1;
                int fieldEnd = nextDelim < 0 ? line.Length : position + nextDelim;
                fieldSpan = line.Slice(position, fieldEnd - position);
                position = nextDelim < 0 ? line.Length + 1 : fieldEnd + 1;
            }

        ProcessField:
            values[columnIndex] = ParseField(fieldSpan.Trim(), kinds[columnIndex], columnIndex, temporalCache, store);
            columnIndex++;
        }

        while (columnIndex < names.Length)
        {
            values[columnIndex] = DataValue.Null(kinds[columnIndex]);
            columnIndex++;
        }
    }

    private static char[] EnsureUnescapeBuffer(int minSize)
    {
        if (_unescapeBuffer is null || _unescapeBuffer.Length < minSize)
        {
            _unescapeBuffer = new char[minSize];
        }
        return _unescapeBuffer;
    }

    /// <summary>
    /// Dispatches a single CSV field span to the appropriate <see cref="DataValue"/>
    /// constructor based on <paramref name="kind"/>. Shared by both the fast (unquoted)
    /// and slow (quoted) parse paths so the same type-handling logic applies regardless
    /// of quoting. <paramref name="temporalCache"/> short-circuits the BCL's flexible
    /// date/time parser for columns with a consistent format.
    /// </summary>
    private static DataValue ParseField(
        ReadOnlySpan<char> field,
        DataKind kind,
        int columnIndex,
        TemporalFormatCache temporalCache,
        IValueStore store)
    {
        if (field.IsEmpty || CsvParser.IsNullLiteral(field))
        {
            return DataValue.Null(kind);
        }

        return kind switch
        {
            DataKind.String => DataValue.FromCharSpan(field, store),
            DataKind.DateTime => temporalCache.TryParseDateTime(field, columnIndex, out DateTimeOffset dt)
                ? DataValue.FromDateTime(dt)
                : DataValue.Null(DataKind.DateTime),
            DataKind.Date => temporalCache.TryParseDate(field, columnIndex, out DateOnly d)
                ? DataValue.FromDate(d)
                : DataValue.Null(DataKind.Date),
            _ => CsvParser.ParseFieldSpan(field, kind),
        };
    }
}
