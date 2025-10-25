using System.Diagnostics;
using System.Runtime.CompilerServices;
using DatumIngest.Ingestion;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Deserializes CSV files into <see cref="RowBatch"/> streams with authoritative
/// per-column type inference by default.
/// </summary>
/// <remarks>
/// Unlike schema-driven formats (Parquet, HDF5) where types are fixed in the file,
/// CSV requires inference. By default this deserializer runs a full-file scan
/// (<see cref="CsvTypeScanner"/>) on the first call to <see cref="DeserializeAsync"/>
/// so all downstream consumers see narrowed, correct types — Int64 narrowed to the
/// smallest fitting width, zero-padded codes preserved as String, dates matched to
/// a specific format, etc. This makes the ingest contract format-agnostic: regardless
/// of source format, rows come in with strict types.
///
/// Callers that don't want the scan (e.g. streaming-only consumers with known schemas)
/// can opt out by passing <c>strictTypes: false</c> to the constructor, which falls
/// back to sample-based inference over the first 100 rows via
/// <see cref="HeaderDetector"/>.
/// </remarks>
public sealed class CsvDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;
    private readonly bool _strictTypes;
    private CsvScanResult? _scanResult;
    private PassMetrics? _scanMetrics;

    /// <summary>
    /// Creates a strict-types CSV deserializer. On first enumeration the full file
    /// is scanned to produce authoritative per-column types.
    /// </summary>
    public CsvDeserializer(FileFormatDescriptor descriptor)
        : this(descriptor, strictTypes: true) { }

    /// <summary>
    /// Creates a CSV deserializer, optionally opting out of the full-file strict-types
    /// scan. When <paramref name="strictTypes"/> is <c>false</c>, column types are
    /// inferred from a 100-row sample via <see cref="HeaderDetector"/> — fast to first
    /// batch, but can misclassify columns where the sample doesn't represent later rows.
    /// </summary>
    public CsvDeserializer(FileFormatDescriptor descriptor, bool strictTypes)
    {
        _descriptor = descriptor;
        _strictTypes = strictTypes;
    }

    /// <summary>
    /// Creates a deserializer from a pre-computed <see cref="CsvScanResult"/>. Use
    /// when the scan was performed separately (e.g. to inspect or log decisions
    /// before ingestion).
    /// </summary>
    public CsvDeserializer(FileFormatDescriptor descriptor, CsvScanResult scanResult)
    {
        _descriptor = descriptor;
        _strictTypes = true;
        _scanResult = scanResult;
    }

    /// <inheritdoc/>
    public PassMetrics? ScanMetrics => _scanMetrics;

    /// <summary>
    /// The scan result populated when <see cref="DeserializeAsync"/> runs in strict mode;
    /// <c>null</c> for non-strict deserializers or before the first enumeration.
    /// </summary>
    public CsvScanResult? ScanResult => _scanResult;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Strict mode: run the full-file scan before opening a stream for enumeration.
        // This is the default path; consumers that need time-to-first-batch latency
        // can disable via the strictTypes ctor flag.
        if (_strictTypes && _scanResult is null)
        {
            Stopwatch scanSw = Stopwatch.StartNew();
            _scanResult = await CsvTypeScanner.ScanAsync(_descriptor, cancellationToken).ConfigureAwait(false);
            scanSw.Stop();
            _scanMetrics = new PassMetrics(
                RowCount: _scanResult.RowCount,
                BatchCount: 0,
                BytesRead: _scanResult.BytesRead,
                ArenaBytesWritten: 0,
                Elapsed: _scanResult.Elapsed);
        }

        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        string[] names;
        DataKind[] kinds;
        bool hasHeader;
        char delimiter;
        TemporalFormatCache temporalCache;

        if (_scanResult is not null)
        {
            if (_scanResult.ColumnNames.Length == 0) yield break;
            names = _scanResult.ColumnNames;
            kinds = _scanResult.Kinds;
            hasHeader = _scanResult.HasHeader;
            delimiter = _scanResult.Delimiter;
            temporalCache = _scanResult.WarmedTemporalCache;
        }
        else
        {
            // Opt-out path: sample-based inference via HeaderDetector.
            delimiter = DelimiterDetector.Detect(stream, _descriptor.Options, _descriptor.FilePath);

            bool? headerOverride = GetHeaderOverride(_descriptor.Options);
            HeaderDetectionResult detection = HeaderDetector.Detect(stream, delimiter, headerOverride);

            if (detection.ColumnNames.Length == 0) yield break;

            names = detection.ColumnNames;
            kinds = detection.ColumnKinds;
            hasHeader = detection.HasHeader;
            temporalCache = new(names.Length);
        }

        ColumnLookup columnLookup = new(names);

        // Stream is at position 0 after open. Create line reader.
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
            batch ??= context.Pool.RentRowBatch(columnLookup, DefaultBatchSize);

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

            batch.Add(values);

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
