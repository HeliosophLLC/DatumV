using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DatumIngest.Functions.Json;
using DatumIngest.Ingestion;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Deserializes newline-delimited JSON (<c>.jsonl</c> / <c>.ndjson</c>) into
/// <see cref="RowBatch"/> streams. Each non-empty line is one independent JSON
/// value. The file's shape is locked on the first non-empty line and every
/// subsequent line must conform:
/// <list type="bullet">
///   <item>If line 1 is a JSON object → object-mode. Each line is one row;
///   columns come from the union of keys across all lines (same inference as
///   the single-file JSON deserializer).</item>
///   <item>If line 1 is anything else (array, string, number, true, false,
///   null) → single-column-mode. The schema is fixed at one column named
///   <c>value</c> of kind <see cref="DataKind.Json"/>; each line lands as one
///   <c>Json</c> cell.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Mid-file shape mismatches throw with the offending line number — mixing an
/// object line into a single-column-mode file (or vice versa) is treated as
/// data-quality error, not silently coerced. Empty / whitespace-only lines are
/// skipped (matches <c>jq -c</c> + <c>jq -s</c> conventions).
/// </para>
/// <para>
/// The implementation is two-pass: pass 1 scans every line to infer the schema
/// (object-mode) or just count rows + lock shape (single-column-mode); pass 2
/// re-opens the stream and emits one row per line. Each line is parsed as its
/// own <see cref="JsonDocument"/>, so memory stays bounded to one line's worth
/// of parsed JSON at a time — no whole-file materialization the way the array
/// <see cref="JsonDeserializer"/> needs.
/// </para>
/// </remarks>
public sealed class JsonLinesDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;
    private PassMetrics? _scanMetrics;

    /// <summary>Creates a JSONL deserializer for the given source descriptor.</summary>
    public JsonLinesDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public PassMetrics? ScanMetrics => _scanMetrics;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ───────── Pass 1: scan ─────────
        Stopwatch scanSw = Stopwatch.StartNew();
        long bytesRead;
        JsonLinesMode mode;
        JsonScanResult? objectScan = null;
        long observedRowCount;

        await using (Stream scanStream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            bytesRead = TryGetLength(scanStream);
            using StreamReader scanReader = new(scanStream);
            mode = DetectShape(scanReader, _descriptor.FilePath, out long firstLineNumber, out string? firstLine);

            // If the file held no non-empty lines, yield nothing — the descriptor
            // is well-formed (an empty .jsonl is valid), there's just nothing to
            // emit.
            if (firstLine is null)
            {
                _scanMetrics = new PassMetrics(0, 0, bytesRead, 0, scanSw.Elapsed);
                yield break;
            }

            if (mode == JsonLinesMode.Object)
            {
                // Run the shared scanner over every line's root object.
                objectScan = JsonTypeScanner.Scan(
                    EnumerateObjectLines(scanReader, _descriptor.FilePath, firstLine, firstLineNumber, cancellationToken),
                    cancellationToken);
                observedRowCount = objectScan.RowCount;
            }
            else
            {
                // Single-column mode: parse each remaining line to validate it's
                // well-formed JSON and not a stray object that would be a shape
                // mismatch. Counting in pass 1 catches errors before any row
                // is yielded — better than discovering them mid-emit.
                long count = 1;
                long currentLine = firstLineNumber;
                while (TryReadNonEmptyLine(scanReader, ref currentLine, out string? line, out long _))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonValueKind kind;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(line!);
                        kind = doc.RootElement.ValueKind;
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException(
                            $"JSONL file '{_descriptor.FilePath}' line {currentLine}: malformed JSON ({ex.Message}).", ex);
                    }
                    if (kind == JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            $"JSONL file '{_descriptor.FilePath}' line {currentLine}: got object, but "
                            + "the file is single-column-mode based on the first line. Mid-file shape "
                            + "switches aren't supported — split into separate files.");
                    }
                    count++;
                }
                observedRowCount = count;
            }
        }

        scanSw.Stop();
        _scanMetrics = new PassMetrics(
            RowCount: observedRowCount,
            BatchCount: 0,
            BytesRead: bytesRead,
            ArenaBytesWritten: 0,
            Elapsed: scanSw.Elapsed);

        if (observedRowCount == 0) yield break;

        // ───────── Pass 2: emit ─────────
        await using Stream emitStream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader emitReader = new(emitStream);

        ColumnLookup lookup;
        string[] columnNames;
        DataKind[] kinds;
        if (mode == JsonLinesMode.Object)
        {
            columnNames = objectScan!.ColumnNames;
            kinds = objectScan.Kinds;
            lookup = new ColumnLookup(columnNames);
        }
        else
        {
            columnNames = ["value"];
            kinds = [DataKind.Json];
            lookup = new ColumnLookup(columnNames);
        }

        RowBatch? batch = null;
        long lineNumber = 0;

        while (TryReadNonEmptyLine(emitReader, ref lineNumber, out string? line, out long _))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using JsonDocument doc = JsonDocument.Parse(line!);
            JsonElement element = doc.RootElement;

            // Re-validate shape on every line — a file that scanned cleanly
            // shouldn't go wonky in pass 2, but a fresh stream could expose
            // an in-flight write race; the line number in the error makes
            // diagnosis cheap.
            if (mode == JsonLinesMode.Object && element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"JSONL file '{_descriptor.FilePath}' line {lineNumber}: expected object "
                    + $"(file is object-mode based on the first line) but got {element.ValueKind}.");
            }
            if (mode == JsonLinesMode.SingleColumn && element.ValueKind == JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"JSONL file '{_descriptor.FilePath}' line {lineNumber}: got object, but "
                    + "the file is single-column-mode based on the first line. Mid-file shape "
                    + "switches aren't supported — split into separate files.");
            }

            DataValue[] values = context.Pool.RentDataValues(columnNames.Length);
            batch ??= context.Pool.RentRowBatch(lookup, DefaultBatchSize);

            if (mode == JsonLinesMode.Object)
            {
                JsonRowMaterializer.FillRow(element, columnNames, kinds, values, batch.Arena);
            }
            else
            {
                // Single-column mode: stash the line's CBOR encoding as the one Json cell.
                byte[] cbor = CborJsonCodec.EncodeFromJsonText(element.GetRawText());
                values[0] = DataValue.FromJson(cbor, batch.Arena);
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

    // ────────────────────── Shape detection ──────────────────────

    private static JsonLinesMode DetectShape(
        StreamReader reader,
        string filePath,
        out long firstLineNumber,
        out string? firstLine)
    {
        long lineNumber = 0;
        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                firstLineNumber = 0;
                firstLine = null;
                return JsonLinesMode.Object;
            }
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonValueKind kind;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                kind = doc.RootElement.ValueKind;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"JSONL file '{filePath}' line {lineNumber}: malformed JSON ({ex.Message}).", ex);
            }

            firstLineNumber = lineNumber;
            firstLine = line;
            return kind == JsonValueKind.Object
                ? JsonLinesMode.Object
                : JsonLinesMode.SingleColumn;
        }
    }

    // ────────────────────── Object-mode line iteration ──────────────────────

    /// <summary>
    /// Iterates object-mode lines for the scan pass. The first line is supplied
    /// separately (it was peeked during shape detection) so we don't re-read
    /// the stream from the start.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateObjectLines(
        StreamReader reader,
        string filePath,
        string firstLine,
        long firstLineNumber,
        CancellationToken cancellationToken)
    {
        // Yield the already-peeked first line.
        {
            using JsonDocument doc = JsonDocument.Parse(firstLine);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"JSONL file '{filePath}' line {firstLineNumber}: expected object but got "
                    + $"{doc.RootElement.ValueKind}.");
            }
            yield return doc.RootElement;
        }

        long lineNumber = firstLineNumber;
        while (TryReadNonEmptyLine(reader, ref lineNumber, out string? line, out long _))
        {
            cancellationToken.ThrowIfCancellationRequested();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line!);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"JSONL file '{filePath}' line {lineNumber}: malformed JSON ({ex.Message}).", ex);
            }

            try
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"JSONL file '{filePath}' line {lineNumber}: expected object "
                        + $"(file is object-mode based on the first line) but got "
                        + $"{doc.RootElement.ValueKind}.");
                }
                yield return doc.RootElement;
            }
            finally
            {
                doc.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the next non-empty line from <paramref name="reader"/>, advancing
    /// <paramref name="lineNumber"/> for each line read (empty or not). Returns
    /// <c>true</c> when a non-empty line was found.
    /// </summary>
    private static bool TryReadNonEmptyLine(
        StreamReader reader,
        ref long lineNumber,
        out string? line,
        out long resultLineNumber)
    {
        while (true)
        {
            string? read = reader.ReadLine();
            if (read is null)
            {
                line = null;
                resultLineNumber = lineNumber;
                return false;
            }
            lineNumber++;
            if (!string.IsNullOrWhiteSpace(read))
            {
                line = read;
                resultLineNumber = lineNumber;
                return true;
            }
        }
    }

    private static long TryGetLength(Stream stream)
    {
        try { return stream.CanSeek ? stream.Length : 0; }
        catch { return 0; }
    }
}

/// <summary>
/// Locked shape of a JSONL file, determined by inspecting the first non-empty line.
/// </summary>
internal enum JsonLinesMode
{
    /// <summary>Every line is a JSON object; columns come from union-of-keys.</summary>
    Object,
    /// <summary>Every line is a non-object JSON value; single column named <c>value</c>.</summary>
    SingleColumn,
}
