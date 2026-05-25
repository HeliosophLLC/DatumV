using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Json;

/// <summary>
/// Deserializes JSON files into <see cref="RowBatch"/> streams. Accepts two root
/// shapes — a single object (1 row) or an array of objects (N rows) — and rejects
/// primitive or array roots. Per-column types are inferred over the whole document
/// via <see cref="JsonTypeScanner"/> before the first batch is yielded, so
/// downstream consumers see narrowed kinds with the same strict-types contract as
/// the CSV deserializer.
/// </summary>
/// <remarks>
/// <para>
/// The document is parsed once via <see cref="JsonDocument.ParseAsync"/> and held
/// for the duration of <see cref="DeserializeAsync"/>. The scan and emit passes
/// share the parsed tape, so iteration is O(rows × columns) without re-parsing.
/// For very large files this trades disk I/O for memory; line-streamed JSONL will
/// follow as a separate format in a later slice.
/// </para>
/// <para>
/// Nested objects/arrays and columns whose scalar values mix across primitive
/// families are kept as <see cref="DataKind.Json"/>. The raw JSON of each such
/// value is re-encoded into canonical CBOR by <see cref="CborJsonCodec"/> and
/// stored in the batch's arena, so equality, hashing, and the <c>json_*</c>
/// function family Just Work.
/// </para>
/// </remarks>
public sealed class JsonDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;
    private readonly JsonScanResult? _precomputedScan;
    private PassMetrics? _scanMetrics;

    /// <summary>Creates a JSON deserializer for the given source descriptor.</summary>
    public JsonDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <summary>
    /// Creates a deserializer from a pre-computed <see cref="JsonScanResult"/>.
    /// Use when the scan was performed separately (e.g. at plan time by a TVF)
    /// so the emit pass skips redoing schema inference.
    /// </summary>
    public JsonDeserializer(FileFormatDescriptor descriptor, JsonScanResult precomputedScan)
    {
        _descriptor = descriptor;
        _precomputedScan = precomputedScan;
    }

    /// <inheritdoc/>
    public PassMetrics? ScanMetrics => _scanMetrics;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        long bytesRead = TryGetLength(stream);

        using JsonDocument document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        // Scan pass: discover schema before emitting any rows. Skipped when a
        // pre-computed scan was supplied to the constructor (e.g. by open_json
        // at plan time, where the schema must be known before execution).
        JsonScanResult scan;
        if (_precomputedScan is not null)
        {
            scan = _precomputedScan;
            _scanMetrics = new PassMetrics(
                RowCount: scan.RowCount,
                BatchCount: 0,
                BytesRead: bytesRead,
                ArenaBytesWritten: 0,
                Elapsed: scan.Elapsed);
        }
        else
        {
            Stopwatch scanSw = Stopwatch.StartNew();
            scan = JsonTypeScanner.Scan(EnumerateRows(root, _descriptor.FilePath), cancellationToken);
            scanSw.Stop();
            _scanMetrics = new PassMetrics(
                RowCount: scan.RowCount,
                BatchCount: 0,
                BytesRead: bytesRead,
                ArenaBytesWritten: 0,
                Elapsed: scanSw.Elapsed);
        }

        if (scan.ColumnNames.Length == 0) yield break;

        ColumnLookup columnLookup = new(scan.ColumnNames);
        RowBatch? batch = null;

        foreach (JsonElement row in EnumerateRows(root, _descriptor.FilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataValue[] values = context.Pool.RentDataValues(scan.ColumnNames.Length);
            batch ??= context.Pool.RentRowBatch(columnLookup, DefaultBatchSize);

            JsonRowMaterializer.FillRow(row, scan.ColumnNames, scan.Kinds, values, batch.Arena);

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

    // ────────────────────── Root shape → row iteration ──────────────────────

    /// <summary>
    /// Iterates the rows of a JSON document. A root object yields exactly one row
    /// (itself); a root array yields each element. Each element must be an object;
    /// primitive or array elements and non-object/non-array roots throw with the
    /// file path in the message so the source of bad input is identifiable.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateRows(JsonElement root, string filePath)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                yield return root;
                yield break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            $"JSON root array in '{filePath}' contains a non-object element at index {index} " +
                            $"(value kind: {element.ValueKind}). Only arrays of objects are supported.");
                    }
                    yield return element;
                    index++;
                }
                yield break;

            default:
                throw new InvalidDataException(
                    $"JSON root in '{filePath}' is {root.ValueKind}; expected an object or an array of objects.");
        }
    }

    // ────────────────────── Helpers ──────────────────────

    private static long TryGetLength(Stream stream)
    {
        try { return stream.CanSeek ? stream.Length : 0; }
        catch { return 0; }
    }
}
