using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Csv;

/// <summary>
/// Single-file CSV writer. Streams rows directly to a UTF-8 file — no per-
/// row-group buffering, no level streams, no schema footer. The first
/// <see cref="WriteAsync"/> call lazily opens the file and (when enabled)
/// writes the header row built from the planner schema. Subsequent calls
/// append rows immediately.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scalar text formats</strong> are picked to match the
/// <see cref="Serialization.Csv.CsvTypeScanner"/>'s inference rules so the
/// <c>COPY → open_csv_typed</c> round trip works without explicit options on
/// read. ISO 8601 dates / timestamps, lowercase <c>true</c>/<c>false</c>,
/// plain invariant-culture numerics, empty field for NULL.
/// </para>
/// <para>
/// <strong>Composite kinds</strong> (<see cref="DataKind.Struct"/> and any
/// array kind) are emitted as JSON text inside a single CSV field. The
/// scanner re-reads them as String columns on import — that's the honest
/// answer; CSV doesn't carry composite-kind metadata. Users who need to
/// round-trip composite values losslessly should export to Parquet.
/// </para>
/// </remarks>
internal sealed class CsvExportSink : IExportSink
{
    private readonly string _path;
    private readonly Schema _schema;
    private readonly SidecarRegistry? _sidecarRegistry;
    private readonly char _delimiter;
    private readonly char _quote;
    private readonly string _lineEnding;
    private readonly string _nullString;
    private readonly bool _writeHeader;
    // Pre-allocated builder, reused across rows. Output is char-oriented
    // (StreamWriter's natural surface) so a StringBuilder is the right
    // intermediate buffer for JSON-encoded composites and quoted fields.
    private readonly StringBuilder _scratch = new(capacity: 256);

    private FileStream? _stream;
    private StreamWriter? _writer;
    private bool _finished;
    private bool _headerWritten;
    private long _finalBytesWritten;
    private int[]? _sourceOrdinals;

    public CsvExportSink(
        string path,
        Schema schema,
        SidecarRegistry? sidecarRegistry,
        char delimiter,
        char quote,
        string lineEnding,
        string nullString,
        bool writeHeader)
    {
        _path = path;
        _schema = schema;
        _sidecarRegistry = sidecarRegistry;
        _delimiter = delimiter;
        _quote = quote;
        _lineEnding = lineEnding;
        _nullString = nullString;
        _writeHeader = writeHeader;
    }

    /// <inheritdoc />
    public long RowsWritten { get; private set; }

    /// <inheritdoc />
    public long BytesWritten => _stream?.Length ?? _finalBytesWritten;

    /// <inheritdoc />
    public async ValueTask WriteAsync(RowBatch batch, CancellationToken cancellationToken)
    {
        if (_finished)
        {
            throw new ExportRuntimeException(
                $"COPY TO csv: sink for '{_path}' has already been finished; " +
                "cannot accept more rows.");
        }
        if (batch.Count == 0) return;

        EnsureWriterOpen();
        StreamWriter writer = _writer!;

        if (!_headerWritten)
        {
            if (_writeHeader)
            {
                WriteHeaderRow(writer);
            }
            _headerWritten = true;
        }

        // Map source-batch column ordinals to target-schema ordinals once.
        // The source query's projection may emit columns in a different order
        // than the schema declares — case-insensitive name lookup via the
        // batch's ColumnLookup is the same pattern the Parquet sink uses.
        ColumnLookup lookup = batch.ColumnLookup;
        int[] sourceOrdinals = _sourceOrdinals ??= new int[_schema.Columns.Count];
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (!lookup.TryGetColumnOrdinal(_schema.Columns[i].Name, out int sourceOrd))
            {
                throw new ExportRuntimeException(
                    $"COPY TO csv: batch is missing expected column '{_schema.Columns[i].Name}'. " +
                    "The source query's projection changed mid-stream.");
            }
            sourceOrdinals[i] = sourceOrd;
        }

        IValueStore store = batch.Arena;
        for (int r = 0; r < batch.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row row = batch[r];
            for (int c = 0; c < _schema.Columns.Count; c++)
            {
                if (c > 0) writer.Write(_delimiter);
                WriteValue(writer, row[sourceOrdinals[c]], store);
            }
            writer.Write(_lineEnding);
            RowsWritten++;
        }

        // Periodic flush so BytesWritten stays close to the actual on-disk
        // size while a long export is in flight (the UI reads BytesWritten
        // off the live sink). Flushing every batch is cheap compared to the
        // per-row work above.
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished) return;
        _finished = true;

        // Even when no rows were written we still want a valid (possibly
        // header-only) CSV file at the target path so callers can distinguish
        // "the export ran" from "the export never started".
        EnsureWriterOpen();
        if (!_headerWritten)
        {
            if (_writeHeader)
            {
                WriteHeaderRow(_writer!);
            }
            _headerWritten = true;
        }

        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_stream is not null)
        {
            _finalBytesWritten = _stream.Length;
        }
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Defensive: an aborted export still needs the handle closed so the
        // partial-file cleanup in ExportPlan can delete the half-written file.
        if (_writer is not null)
        {
            try { await _writer.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow on abort */ }
            _writer = null;
        }
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private void EnsureWriterOpen()
    {
        if (_writer is not null) return;
        _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        // UTF-8 without BOM. The BOM trips up several CSV consumers (notably
        // older pandas), and the scanner doesn't require it. StreamWriter's
        // default buffer is fine here — rows are short enough that the
        // delimiter / line-ending writes get coalesced naturally.
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteHeaderRow(StreamWriter writer)
    {
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (i > 0) writer.Write(_delimiter);
            WriteField(writer, _schema.Columns[i].Name);
        }
        writer.Write(_lineEnding);
    }

    private void WriteValue(StreamWriter writer, DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            // The NULL representation. Default is empty — written as zero
            // characters between two delimiters, which matches both PG COPY
            // and the CsvTypeScanner's null-inference. A non-empty NULL_STRING
            // still passes through WriteField so a literal " " or "NULL" gets
            // quoted if it happens to contain the delimiter.
            if (_nullString.Length > 0)
            {
                WriteField(writer, _nullString);
            }
            return;
        }

        // Composite kinds — emit as JSON text in a single CSV field.
        if (value.IsArray || value.Kind == DataKind.Struct)
        {
            _scratch.Clear();
            DataValueJsonWriter.WriteValue(_scratch, value, store, _sidecarRegistry);
            WriteField(writer, _scratch);
            return;
        }

        switch (value.Kind)
        {
            case DataKind.Boolean:
                writer.Write(value.AsBoolean() ? "true" : "false");
                break;

            case DataKind.UInt8: writer.Write(value.AsUInt8()); break;
            case DataKind.UInt16: writer.Write(value.AsUInt16()); break;
            case DataKind.UInt32: writer.Write(value.AsUInt32()); break;
            case DataKind.UInt64: writer.Write(value.AsUInt64()); break;
            case DataKind.UInt128:
                writer.Write(value.AsUInt128().ToString(CultureInfo.InvariantCulture));
                break;
            case DataKind.Int8: writer.Write(value.AsInt8()); break;
            case DataKind.Int16: writer.Write(value.AsInt16()); break;
            case DataKind.Int32: writer.Write(value.AsInt32()); break;
            case DataKind.Int64: writer.Write(value.AsInt64()); break;
            case DataKind.Int128:
                writer.Write(value.AsInt128().ToString(CultureInfo.InvariantCulture));
                break;

            case DataKind.Float16:
                writer.Write(((float)value.AsFloat16()).ToString("R", CultureInfo.InvariantCulture));
                break;
            case DataKind.Float32:
                writer.Write(value.AsFloat32().ToString("R", CultureInfo.InvariantCulture));
                break;
            case DataKind.Float64:
                writer.Write(value.AsFloat64().ToString("R", CultureInfo.InvariantCulture));
                break;
            case DataKind.Decimal:
                writer.Write(value.AsDecimal().ToString(CultureInfo.InvariantCulture));
                break;

            case DataKind.Date:
                writer.Write(value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DataKind.Time:
                writer.Write(value.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.Timestamp:
                writer.Write(value.AsTimestamp().ToString(
                    "yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.TimestampTz:
                // "O" produces a round-trippable ISO 8601 form with offset; the
                // engine stores TimestampTz as UTC ticks so the offset is always
                // +00:00 on the way out — that's accurate, not a fidelity loss.
                writer.Write(value.AsTimestampTz().ToString("O", CultureInfo.InvariantCulture));
                break;
            case DataKind.Duration:
                writer.Write(value.AsDuration().ToString("c", CultureInfo.InvariantCulture));
                break;

            case DataKind.String:
                // Pass the SidecarRegistry — real ingested datasets store
                // String bytes (NYC taxi's `file_name`, COCO's `file` paths
                // etc.) in a .datum-blob sidecar, and AsString without the
                // registry throws on those. Same regression the Parquet
                // sink fixed.
                WriteField(writer, value.AsString(store, _sidecarRegistry));
                break;
            case DataKind.Uuid:
                writer.Write(value.AsUuid().ToString("D"));
                break;

            case DataKind.Json:
                // CBOR on the wire; decode to plain JSON text in the cell so
                // pandas / Excel / DuckDB see a readable string column.
                WriteField(writer, CborJsonCodec.DecodeToJsonText(value.AsByteSpan(store, _sidecarRegistry)));
                break;

            // Inline visual / spatial scalars: emit as JSON object so they
            // remain inspectable and structurally explicit. Re-import as
            // String via open_csv_typed — round-trip back to the original
            // kind requires Parquet.
            case DataKind.Color:
            case DataKind.Point2D:
            case DataKind.Point3D:
                _scratch.Clear();
                DataValueJsonWriter.WriteValue(_scratch, value, store, _sidecarRegistry);
                WriteField(writer, _scratch);
                break;

            default:
                // All representable kinds are covered above; CsvExportFormat
                // rejects the rest at plan time. If we land here, it's a
                // schema-reconciliation surprise (the runtime kind diverged
                // from the planner kind into a kind the format rejects).
                throw new ExportRuntimeException(
                    $"COPY TO csv: cannot encode value of kind {value.Kind} — " +
                    "plan-time validation should have rejected this column. " +
                    "Surface this as a bug.");
        }
    }

    /// <summary>
    /// RFC 4180 field writer for a <see cref="string"/>. Quotes the field when
    /// it contains the delimiter, the quote character, or any newline; doubles
    /// the quote character inside the quoted body.
    /// </summary>
    private void WriteField(StreamWriter writer, string value)
        => WriteField(writer, value.AsSpan());

    private void WriteField(StreamWriter writer, StringBuilder value)
    {
        // StringBuilder doesn't expose a span over its whole contents without
        // copying for the multi-chunk case. Rows of composite JSON aren't
        // pathological, but to keep the API uniform we route through
        // ToString() — the StringBuilder is reused across rows so the
        // allocation cost is per-composite-field, not per-row.
        WriteField(writer, value.ToString());
    }

    private void WriteField(StreamWriter writer, ReadOnlySpan<char> value)
    {
        if (!NeedsQuoting(value))
        {
            writer.Write(value);
            return;
        }
        writer.Write(_quote);
        foreach (char c in value)
        {
            if (c == _quote) writer.Write(_quote);
            writer.Write(c);
        }
        writer.Write(_quote);
    }

    private bool NeedsQuoting(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c == _delimiter || c == _quote || c == '\n' || c == '\r') return true;
        }
        return false;
    }
}

/// <summary>
/// Renders a <see cref="DataValue"/> into a <see cref="StringBuilder"/> as
/// JSON text. Used by <see cref="CsvExportSink"/> to serialise composite
/// kinds (Struct, Array&lt;T&gt;) and the inline visual / spatial scalars
/// (<see cref="DataKind.Color"/>, <see cref="DataKind.Point2D"/>,
/// <see cref="DataKind.Point3D"/>) into a single CSV field.
/// </summary>
/// <remarks>
/// Routes through <see cref="Utf8JsonWriter"/> on a pooled
/// <see cref="ArrayBufferWriter{T}"/> for canonical encoding (proper
/// string escapes, infinity / NaN handling, no culture-sensitive output) and
/// then transcodes the UTF-8 bytes to chars in the target builder. The
/// transcode is single-pass and zero-managed-allocation beyond the buffer
/// writer rental.
/// </remarks>
internal static class DataValueJsonWriter
{
    public static void WriteValue(
        StringBuilder destination,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry)
    {
        ArrayBufferWriter<byte> bytes = new(initialCapacity: 64);
        using (Utf8JsonWriter writer = new(bytes, new JsonWriterOptions
        {
            // Inline JSON inside a CSV cell — indentation would just inflate
            // the file with no readability win after quoting.
            Indented = false,
            // Don't escape non-ASCII via \uXXXX. The CSV is UTF-8 and the
            // field writer already handles the delimiter / quote / newline
            // cases that matter for CSV parsing.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteValueInternal(writer, value, store, registry);
        }
        // ArrayBufferWriter holds UTF-8 bytes; StringBuilder.Append is char-
        // oriented. UTF8Encoding.GetString does the decode in one pass without
        // intermediate string allocation beyond the result.
        destination.Append(Encoding.UTF8.GetString(bytes.WrittenSpan));
    }

    private static void WriteValueInternal(
        Utf8JsonWriter writer,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry)
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsArray)
        {
            WriteArray(writer, value, store, registry);
            return;
        }

        switch (value.Kind)
        {
            case DataKind.Boolean:
                writer.WriteBooleanValue(value.AsBoolean());
                break;

            case DataKind.UInt8: writer.WriteNumberValue(value.AsUInt8()); break;
            case DataKind.UInt16: writer.WriteNumberValue(value.AsUInt16()); break;
            case DataKind.UInt32: writer.WriteNumberValue(value.AsUInt32()); break;
            case DataKind.UInt64: writer.WriteNumberValue(value.AsUInt64()); break;
            case DataKind.Int8: writer.WriteNumberValue(value.AsInt8()); break;
            case DataKind.Int16: writer.WriteNumberValue(value.AsInt16()); break;
            case DataKind.Int32: writer.WriteNumberValue(value.AsInt32()); break;
            case DataKind.Int64: writer.WriteNumberValue(value.AsInt64()); break;
            case DataKind.Int128:
                writer.WriteStringValue(value.AsInt128().ToString(CultureInfo.InvariantCulture));
                break;
            case DataKind.UInt128:
                writer.WriteStringValue(value.AsUInt128().ToString(CultureInfo.InvariantCulture));
                break;

            case DataKind.Float16:
                writer.WriteNumberValue((float)value.AsFloat16());
                break;
            case DataKind.Float32:
                writer.WriteNumberValue(value.AsFloat32());
                break;
            case DataKind.Float64:
                writer.WriteNumberValue(value.AsFloat64());
                break;
            case DataKind.Decimal:
                writer.WriteNumberValue(value.AsDecimal());
                break;

            case DataKind.String:
                writer.WriteStringValue(value.AsString(store, registry));
                break;
            case DataKind.Uuid:
                writer.WriteStringValue(value.AsUuid().ToString("D"));
                break;
            case DataKind.Date:
                writer.WriteStringValue(value.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DataKind.Time:
                writer.WriteStringValue(value.AsTime().ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.Timestamp:
                writer.WriteStringValue(value.AsTimestamp().ToString(
                    "yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.TimestampTz:
                writer.WriteStringValue(value.AsTimestampTz().ToString("O", CultureInfo.InvariantCulture));
                break;
            case DataKind.Duration:
                writer.WriteStringValue(value.AsDuration().ToString("c", CultureInfo.InvariantCulture));
                break;

            case DataKind.Color:
                {
                    (byte r, byte g, byte b, byte a) = value.AsColor();
                    writer.WriteStartObject();
                    writer.WriteNumber("r", r);
                    writer.WriteNumber("g", g);
                    writer.WriteNumber("b", b);
                    writer.WriteNumber("a", a);
                    writer.WriteEndObject();
                    break;
                }

            case DataKind.Point2D:
                {
                    System.Numerics.Vector2 v = value.AsPoint2D();
                    writer.WriteStartObject();
                    writer.WriteNumber("x", v.X);
                    writer.WriteNumber("y", v.Y);
                    writer.WriteEndObject();
                    break;
                }

            case DataKind.Point3D:
                {
                    System.Numerics.Vector3 v = value.AsPoint3D();
                    writer.WriteStartObject();
                    writer.WriteNumber("x", v.X);
                    writer.WriteNumber("y", v.Y);
                    writer.WriteNumber("z", v.Z);
                    writer.WriteEndObject();
                    break;
                }

            case DataKind.Struct:
                WriteStruct(writer, value, store, registry);
                break;

            case DataKind.Json:
                // Inline the canonical JSON text decoded from the CBOR
                // payload so a JSON-valued struct field reads as an actual
                // nested object, not a stringified blob.
                WriteRawJson(writer, CborJsonCodec.DecodeToJsonText(value.AsByteSpan(store, registry)));
                break;

            default:
                // Typed-media and runtime-only kinds are rejected at plan
                // time. If we still see one, fall back to the kind name as a
                // string — better than throwing inside a JSON serializer.
                writer.WriteStringValue($"<{value.Kind}>");
                break;
        }
    }

    private static void WriteStruct(
        Utf8JsonWriter writer,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry)
    {
        DataValue[] fieldValues = value.AsStruct(store);
        writer.WriteStartObject();
        for (int i = 0; i < fieldValues.Length; i++)
        {
            // Struct field names aren't carried on the value itself — they
            // live on the enclosing ColumnInfo / TypeDescriptor. The sink
            // doesn't thread the descriptor down because struct values inside
            // arrays can have heterogeneous descriptors. Falling back to
            // f0, f1, … gives a stable, inspectable shape on disk; users
            // who need named struct fields should export to Parquet.
            writer.WritePropertyName($"f{i}");
            WriteValueInternal(writer, fieldValues[i], store, registry);
        }
        writer.WriteEndObject();
    }

    private static void WriteArray(
        Utf8JsonWriter writer,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry)
    {
        writer.WriteStartArray();
        switch (value.Kind)
        {
            case DataKind.Boolean:
                foreach (bool b in value.AsArraySpan<bool>(store, registry))
                    writer.WriteBooleanValue(b);
                break;
            case DataKind.UInt8:
                foreach (byte b in value.AsArraySpan<byte>(store, registry))
                    writer.WriteNumberValue(b);
                break;
            case DataKind.UInt16:
                foreach (ushort u in value.AsArraySpan<ushort>(store, registry))
                    writer.WriteNumberValue(u);
                break;
            case DataKind.UInt32:
                foreach (uint u in value.AsArraySpan<uint>(store, registry))
                    writer.WriteNumberValue(u);
                break;
            case DataKind.UInt64:
                foreach (ulong u in value.AsArraySpan<ulong>(store, registry))
                    writer.WriteNumberValue(u);
                break;
            case DataKind.Int8:
                foreach (sbyte s in value.AsArraySpan<sbyte>(store, registry))
                    writer.WriteNumberValue(s);
                break;
            case DataKind.Int16:
                foreach (short s in value.AsArraySpan<short>(store, registry))
                    writer.WriteNumberValue(s);
                break;
            case DataKind.Int32:
                foreach (int n in value.AsArraySpan<int>(store, registry))
                    writer.WriteNumberValue(n);
                break;
            case DataKind.Int64:
                foreach (long n in value.AsArraySpan<long>(store, registry))
                    writer.WriteNumberValue(n);
                break;
            case DataKind.Float32:
                foreach (float f in value.AsArraySpan<float>(store, registry))
                    writer.WriteNumberValue(f);
                break;
            case DataKind.Float64:
                foreach (double d in value.AsArraySpan<double>(store, registry))
                    writer.WriteNumberValue(d);
                break;
            case DataKind.String:
                foreach (string s in value.AsStringArray(store, registry))
                    writer.WriteStringValue(s);
                break;
            case DataKind.Struct:
                {
                    DataValue[] elements = value.AsStructArray(store, registry);
                    for (int i = 0; i < elements.Length; i++)
                    {
                        WriteValueInternal(writer, elements[i], store, registry);
                    }
                    break;
                }
            default:
                // Array element kinds that have no fixed-stride CLR primitive
                // backing (Date / Time / Timestamp / TimestampTz / Decimal /
                // Uuid / Duration / Json) currently route through here.
                // Parquet's own v1 sink covers the same subset; broader array
                // support is paired across both formats when added.
                writer.WriteStringValue($"<Array<{value.Kind}> not encodable in CSV>");
                break;
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Inserts the parsed structure of <paramref name="jsonText"/> into the
    /// writer's current position. Used so a <see cref="DataKind.Json"/>
    /// value inside a struct surfaces as a real nested JSON object rather
    /// than an escaped string.
    /// </summary>
    private static void WriteRawJson(Utf8JsonWriter writer, string jsonText)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        doc.RootElement.WriteTo(writer);
    }
}
