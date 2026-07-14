using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Json;

/// <summary>
/// Single-file JSON / JSONL writer. Streams rows directly to a UTF-8 file
/// through a single <see cref="Utf8JsonWriter"/>. In array mode the writer
/// opens with a top-level <c>[</c> on the first non-empty batch and closes
/// with <c>]</c> at <see cref="FinishAsync"/>. In JSONL mode each row writes
/// a complete object followed by <c>\n</c>, then the writer is
/// <see cref="Utf8JsonWriter.Reset()"/>'d so the next row can start a fresh
/// top-level value.
/// </summary>
/// <remarks>
/// <para>
/// Struct field names come from the projected <see cref="ColumnInfo.Fields"/>
/// metadata — the same source <c>QuerySchemaResolver</c> populates for the
/// Parquet sink's <c>StructColumnEncoder</c>. Nested struct columns recurse
/// through their <c>Fields</c>; <c>Array&lt;Struct&gt;</c> uses the parent
/// column's <c>Fields</c> for every element (all elements of an array share
/// the same struct shape at the schema level). When <c>Fields</c> is null —
/// a schema-reconciled column whose runtime kind diverged from the planner
/// kind, or a deeply-nested struct the resolver couldn't reach — the writer
/// falls back to positional <c>f0</c>, <c>f1</c>, … names. Same fallback
/// <c>WebCellFormatter</c> uses for the in-UI struct preview.
/// </para>
/// <para>
/// <strong>JsonExportSink.RowsWritten / BytesWritten</strong>: rows is the
/// per-row counter incremented inside <see cref="WriteAsync"/>. Bytes is
/// the live <see cref="FileStream.Length"/> while the file is open and the
/// captured final length after <see cref="FinishAsync"/> closes it — same
/// pattern the Parquet and CSV sinks use.
/// </para>
/// </remarks>
internal sealed class JsonExportSink : IExportSink
{
    private readonly string _path;
    private readonly Schema _schema;
    private readonly SidecarRegistry? _sidecarRegistry;
    private readonly bool _lines;
    private readonly bool _indent;
    private readonly TimeZoneInfo? _sessionTimeZone;

    private FileStream? _stream;
    private Utf8JsonWriter? _writer;
    private bool _finished;
    private bool _arrayOpened;
    private long _finalBytesWritten;
    private int[]? _sourceOrdinals;

    public JsonExportSink(
        string path,
        Schema schema,
        SidecarRegistry? sidecarRegistry,
        bool lines,
        bool indent,
        TimeZoneInfo? sessionTimeZone = null)
    {
        _path = path;
        _schema = schema;
        _sidecarRegistry = sidecarRegistry;
        _lines = lines;
        _indent = indent;
        _sessionTimeZone = sessionTimeZone;
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
                $"COPY TO json: sink for '{_path}' has already been finished; " +
                "cannot accept more rows.");
        }
        if (batch.Count == 0) return;

        EnsureWriterOpen();
        Utf8JsonWriter writer = _writer!;
        FileStream stream = _stream!;

        if (!_arrayOpened && !_lines)
        {
            // Array mode opens with `[` once. JSONL mode skips this — each
            // row is a complete top-level value.
            writer.WriteStartArray();
            _arrayOpened = true;
        }

        // Map source-batch column ordinals to target-schema ordinals once.
        // Same case-insensitive lookup pattern as the Parquet / CSV sinks.
        ColumnLookup lookup = batch.ColumnLookup;
        int[] sourceOrdinals = _sourceOrdinals ??= new int[_schema.Columns.Count];
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (!lookup.TryGetColumnOrdinal(_schema.Columns[i].Name, out int sourceOrd))
            {
                throw new ExportRuntimeException(
                    $"COPY TO json: batch is missing expected column '{_schema.Columns[i].Name}'. " +
                    "The source query's projection changed mid-stream.");
            }
            sourceOrdinals[i] = sourceOrd;
        }

        IValueStore store = batch.Arena;
        for (int r = 0; r < batch.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row row = batch[r];

            writer.WriteStartObject();
            for (int c = 0; c < _schema.Columns.Count; c++)
            {
                ColumnInfo col = _schema.Columns[c];
                writer.WritePropertyName(col.Name);
                WriteSchemaAwareValue(
                    writer, row[sourceOrdinals[c]], col, store, _sidecarRegistry, _sessionTimeZone);
            }
            writer.WriteEndObject();
            RowsWritten++;

            if (_lines)
            {
                // Flush this row to the stream, append the newline
                // separator, and reset the writer so the next row can begin
                // a fresh top-level value. Without Reset() the writer would
                // throw on the next WriteStartObject because it sees a
                // completed top-level value.
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.WriteByte((byte)'\n');
                writer.Reset();
            }
        }

        // Periodic flush so BytesWritten stays close to the live on-disk
        // size for the UI's per-second progress read. The writer's internal
        // buffer batches up to ~16 KB by default; flushing per batch is
        // cheap relative to the per-row work above.
        if (!_lines)
        {
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished) return;
        _finished = true;

        // Even with no rows we still produce a valid file at the target so
        // callers can distinguish "the export ran" from "the export never
        // started". Array mode → "[]"; JSONL mode → empty file.
        EnsureWriterOpen();
        if (!_lines)
        {
            if (!_arrayOpened)
            {
                _writer!.WriteStartArray();
                _arrayOpened = true;
            }
            _writer!.WriteEndArray();
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
        // Defensive cleanup on abort. The partial-file delete in ExportPlan
        // takes care of removing the half-written file from the catalog
        // view; we just close the handle here.
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
        _writer = new Utf8JsonWriter(_stream, new JsonWriterOptions
        {
            Indented = _indent,
            // Don't escape non-ASCII via \uXXXX. The output is UTF-8 and
            // most consumers prefer the literal codepoint over an escape.
            // Same setting CSV's composite-cell writer uses.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Skipped validation: the sink is the only writer of this
            // stream and we never compose pre-encoded JSON fragments at
            // the top level, so the per-call structural check is wasted
            // work in the hot path.
            SkipValidation = false,
        });
    }

    /// <summary>
    /// Writes <paramref name="value"/> with <paramref name="column"/>'s
    /// schema metadata in scope. For struct values this means real field
    /// names; for array-of-struct it threads the parent column's
    /// <c>Fields</c> down to the per-element struct writer. Scalars and
    /// primitive arrays ignore the column argument — their JSON form is
    /// determined entirely by the value kind.
    /// </summary>
    internal static void WriteSchemaAwareValue(
        Utf8JsonWriter writer,
        DataValue value,
        ColumnInfo column,
        IValueStore store,
        SidecarRegistry? registry,
        TimeZoneInfo? sessionZone = null)
    {
        if (value.IsNull)
        {
            writer.WriteNullValue();
            return;
        }
        if (value.IsArray)
        {
            WriteArray(writer, value, column, store, registry, sessionZone);
            return;
        }
        if (value.Kind == DataKind.Struct)
        {
            WriteStruct(writer, value, column.Fields, store, registry, sessionZone);
            return;
        }
        WriteScalar(writer, value, store, registry, sessionZone);
    }

    private static void WriteStruct(
        Utf8JsonWriter writer,
        DataValue value,
        IReadOnlyList<ColumnInfo>? fields,
        IValueStore store,
        SidecarRegistry? registry,
        TimeZoneInfo? sessionZone)
    {
        DataValue[] fieldValues = value.AsStruct(store);
        writer.WriteStartObject();
        for (int i = 0; i < fieldValues.Length; i++)
        {
            // Schema metadata is the canonical name source. When the
            // projection didn't carry per-field info (a model invocation
            // whose return shape wasn't statically resolvable, or a struct
            // built from an untyped literal), fall back to f0, f1, … so
            // the file is still valid JSON with stable shape.
            ColumnInfo? fieldCol = fields is { } f && i < f.Count ? f[i] : null;
            string fieldName = fieldCol?.Name ?? $"f{i}";
            writer.WritePropertyName(fieldName);
            if (fieldCol is not null)
            {
                WriteSchemaAwareValue(writer, fieldValues[i], fieldCol, store, registry, sessionZone);
            }
            else
            {
                // No schema metadata for this field — recurse via the
                // structureless path. Nested structs inside this field
                // will themselves fall back to f0, f1, … which is the
                // honest answer when the shape isn't carried.
                WriteValueWithoutSchema(writer, fieldValues[i], store, registry, sessionZone);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteArray(
        Utf8JsonWriter writer,
        DataValue value,
        ColumnInfo column,
        IValueStore store,
        SidecarRegistry? registry,
        TimeZoneInfo? sessionZone)
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
                    // All elements share the parent column's struct shape —
                    // pass column.Fields through to every element's writer.
                    for (int i = 0; i < elements.Length; i++)
                    {
                        WriteStruct(writer, elements[i], column.Fields, store, registry, sessionZone);
                    }
                    break;
                }
            default:
                // Date / Time / Timestamp / TimestampTz / Decimal / Uuid /
                // Duration / Json as array element kinds have no fixed-
                // stride CLR primitive backing in v1. Surfacing a string
                // placeholder keeps the document well-formed; broader
                // array element support is paired across CSV and Parquet
                // when added.
                writer.WriteStringValue($"<Array<{value.Kind}> not encodable in JSON>");
                break;
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Scalar writer for kinds whose JSON shape is determined entirely by
    /// the value (no schema metadata needed). Pulled out so the schema-
    /// aware path can dispatch cleanly into scalars without re-checking
    /// IsArray / IsStruct.
    /// </summary>
    private static void WriteScalar(
        Utf8JsonWriter writer,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry,
        TimeZoneInfo? sessionZone)
    {
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
            // 128-bit ints have no JSON-number representation that
            // consumers can read back losslessly; stringify them.
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
                writer.WriteStringValue(value.AsDate().ToString(
                    "yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DataKind.Time:
                writer.WriteStringValue(value.AsTime().ToString(
                    "HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.Timestamp:
                writer.WriteStringValue(value.AsTimestamp().ToString(
                    "yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                break;
            case DataKind.TimestampTz:
                writer.WriteStringValue(TemporalSemantics.ProjectForDisplay(
                        value.AsTimestampTz(), sessionZone)
                    .ToString("O", CultureInfo.InvariantCulture));
                break;
            case DataKind.Duration:
                writer.WriteStringValue(value.AsDuration().ToString(
                    "c", CultureInfo.InvariantCulture));
                break;
            case DataKind.Interval:
                writer.WriteStringValue(value.AsInterval().Format());
                break;

            case DataKind.Json:
                // Decode CBOR → JSON text → re-parse into the writer's
                // tree so a Json-typed value surfaces as a real nested
                // object, not an opaque escaped string. Same approach
                // WebCellFormatter uses for the struct-cell renderer.
                WriteRawJson(writer, CborJsonCodec.DecodeToJsonText(
                    value.AsByteSpan(store, registry)));
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

            default:
                // Typed-media and runtime-only kinds are rejected at plan
                // time. If we land here anyway, emit a placeholder so the
                // document stays well-formed.
                writer.WriteStringValue($"<{value.Kind}>");
                break;
        }
    }

    /// <summary>
    /// Recurses into a struct whose <see cref="ColumnInfo.Fields"/> isn't
    /// available — every nested struct inside this subtree uses the
    /// positional <c>fN</c> fallback. Primitive arrays and scalars are
    /// unaffected (their JSON shape doesn't need schema metadata).
    /// </summary>
    private static void WriteValueWithoutSchema(
        Utf8JsonWriter writer,
        DataValue value,
        IValueStore store,
        SidecarRegistry? registry,
        TimeZoneInfo? sessionZone)
    {
        if (value.IsNull) { writer.WriteNullValue(); return; }
        if (value.IsArray)
        {
            // Synthesize a kind-only ColumnInfo so the array writer's
            // Array<Struct> branch can still find its child shape via
            // the value's TypeId-less fallback (positional names inside
            // each element). Other array branches don't read Fields.
            ColumnInfo synthesized = new("", value.Kind, nullable: true) { IsArray = true };
            WriteArray(writer, value, synthesized, store, registry, sessionZone);
            return;
        }
        if (value.Kind == DataKind.Struct)
        {
            WriteStruct(writer, value, fields: null, store, registry, sessionZone);
            return;
        }
        WriteScalar(writer, value, store, registry, sessionZone);
    }

    /// <summary>
    /// Splices the parsed structure of <paramref name="jsonText"/> into
    /// the writer at its current position. Used for the
    /// <see cref="DataKind.Json"/> → real-nested-object inlining.
    /// </summary>
    private static void WriteRawJson(Utf8JsonWriter writer, string jsonText)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        doc.RootElement.WriteTo(writer);
    }
}
