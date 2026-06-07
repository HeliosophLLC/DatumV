using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Export.Parquet;

/// <summary>
/// Per-column accumulator that buffers row values until the sink flushes a
/// row group, then materialises them as a typed <see cref="DataColumn"/>.
/// One <see cref="ParquetColumnEncoder"/> per output column for the lifetime
/// of an <see cref="ParquetExportSink"/>.
/// </summary>
internal abstract class ParquetColumnEncoder
{
    /// <summary>
    /// Parquet field metadata — name, CLR type, nullability, array flag.
    /// Typed as <see cref="Field"/> rather than <see cref="DataField"/> so
    /// struct encoders can return a <see cref="StructField"/> and let
    /// <see cref="ParquetSchema"/> wire the children in. Primitive-leaf
    /// encoders return a <see cref="DataField"/>; the implicit upcast keeps
    /// downstream code unchanged.
    /// </summary>
    public abstract Field Field { get; }

    /// <summary>Append a single <see cref="DataValue"/> to the column buffer.</summary>
    public abstract void Append(DataValue value, IValueStore store);

    /// <summary>
    /// Materialise the accumulated rows as a <see cref="DataColumn"/> and
    /// hand it to <paramref name="rg"/>. The buffer is reset afterwards so
    /// the encoder is ready for the next row group. The sink reads
    /// <see cref="ColumnMetadata"/> separately and passes it to the
    /// three-argument <c>WriteColumnAsync</c> overload — the encoder is
    /// not responsible for the metadata wiring.
    /// </summary>
    public abstract Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken);

    /// <summary>Number of rows currently buffered.</summary>
    public abstract int Count { get; }

    /// <summary>
    /// Approximate byte footprint of the data currently buffered, used by
    /// the sink to flush row groups before per-column buffers grow past
    /// the Parquet writer's 2 GB-per-flush ceiling. Default implementation
    /// reports zero — scalar encoders that hold small fixed-width values
    /// can safely fall through to the row-count threshold; encoders that
    /// hold variable-length blobs (Image / Audio / Video / Mesh /
    /// PointCloud) override.
    /// </summary>
    public virtual long BufferedBytes => 0L;

    /// <summary>
    /// Optional per-column-chunk metadata written alongside this column on
    /// every row-group flush, surfaced through Parquet.Net's three-arg
    /// <c>WriteColumnAsync</c>. Used by typed-media encoders to embed
    /// <c>datumv.kind</c> / <c>datumv.format</c> / <c>datumv.version</c>
    /// so <see cref="Functions.TableValued.OpenParquetFunction"/> can
    /// route the column back to its original <see cref="Model.DataKind"/>
    /// on re-import — the round-trip that closes the
    /// <c>COPY → open_parquet</c> loop for naive consumers without
    /// requiring an explicit <c>mesh_from_gltf</c> / <c>pointcloud_from_ply</c>
    /// wrapper.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string>? ColumnMetadata => null;

    /// <summary>
    /// Shared builder for the <c>datumv.kind</c> / <c>datumv.format</c> /
    /// <c>datumv.version</c> metadata block. Returns the mutable
    /// <see cref="Dictionary{TKey,TValue}"/> Parquet.Net's three-arg
    /// <c>WriteColumnAsync</c> wants — encoders surface it as an
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> through the
    /// <see cref="ColumnMetadata"/> property so the contract stays
    /// read-only externally. Returns <see langword="null"/> when either
    /// argument is null, leaving the column un-tagged.
    /// </summary>
    protected static Dictionary<string, string>? BuildDatumvMetadata(
        string? datumKind, string? datumFormat)
    {
        if (datumKind is null || datumFormat is null) return null;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ParquetDatumvMetadata.KindKey] = datumKind,
            [ParquetDatumvMetadata.FormatKey] = datumFormat,
            [ParquetDatumvMetadata.VersionKey] = ParquetDatumvMetadata.CurrentVersion,
        };
    }

    /// <summary>
    /// Returns the encoder that <paramref name="column"/> requires. Throws
    /// <see cref="ExportPlanException"/> for kinds the v1 Parquet sink does
    /// not yet implement; the planner mirrors the supported set so this is
    /// only hit by genuinely-unsupported columns.
    /// <paramref name="sidecarRegistry"/> is captured by the extractor
    /// closure for typed-media kinds so sidecar-backed values can resolve
    /// their <c>storeId</c>. Pass <see langword="null"/> only at plan-time
    /// validation calls (no byte reads happen there).
    /// </summary>
    public static ParquetColumnEncoder Create(ColumnInfo column, SidecarRegistry? sidecarRegistry)
    {
        string name = column.Name;
        bool nullable = column.Nullable;

        if (column.IsArray)
        {
            return CreateArrayEncoder(column, name, nullable, sidecarRegistry);
        }

        return column.Kind switch
        {
            DataKind.Boolean => nullable
                ? new NullableValueTypeEncoder<bool>(name, static (v, _) => v.AsBoolean())
                : new ValueTypeEncoder<bool>(name, static (v, _) => v.AsBoolean()),

            DataKind.Int8 => nullable
                ? new NullableValueTypeEncoder<sbyte>(name, static (v, _) => v.AsInt8())
                : new ValueTypeEncoder<sbyte>(name, static (v, _) => v.AsInt8()),

            DataKind.Int16 => nullable
                ? new NullableValueTypeEncoder<short>(name, static (v, _) => v.AsInt16())
                : new ValueTypeEncoder<short>(name, static (v, _) => v.AsInt16()),

            DataKind.Int32 => nullable
                ? new NullableValueTypeEncoder<int>(name, static (v, _) => v.AsInt32())
                : new ValueTypeEncoder<int>(name, static (v, _) => v.AsInt32()),

            DataKind.Int64 => nullable
                ? new NullableValueTypeEncoder<long>(name, static (v, _) => v.AsInt64())
                : new ValueTypeEncoder<long>(name, static (v, _) => v.AsInt64()),

            DataKind.UInt8 => nullable
                ? new NullableValueTypeEncoder<byte>(name, static (v, _) => v.AsUInt8())
                : new ValueTypeEncoder<byte>(name, static (v, _) => v.AsUInt8()),

            DataKind.UInt16 => nullable
                ? new NullableValueTypeEncoder<ushort>(name, static (v, _) => v.AsUInt16())
                : new ValueTypeEncoder<ushort>(name, static (v, _) => v.AsUInt16()),

            DataKind.UInt32 => nullable
                ? new NullableValueTypeEncoder<uint>(name, static (v, _) => v.AsUInt32())
                : new ValueTypeEncoder<uint>(name, static (v, _) => v.AsUInt32()),

            DataKind.UInt64 => nullable
                ? new NullableValueTypeEncoder<ulong>(name, static (v, _) => v.AsUInt64())
                : new ValueTypeEncoder<ulong>(name, static (v, _) => v.AsUInt64()),

            DataKind.Float32 => nullable
                ? new NullableValueTypeEncoder<float>(name, static (v, _) => v.AsFloat32())
                : new ValueTypeEncoder<float>(name, static (v, _) => v.AsFloat32()),

            DataKind.Float64 => nullable
                ? new NullableValueTypeEncoder<double>(name, static (v, _) => v.AsFloat64())
                : new ValueTypeEncoder<double>(name, static (v, _) => v.AsFloat64()),

            DataKind.Decimal => nullable
                ? new NullableValueTypeEncoder<decimal>(name, static (v, _) => v.AsDecimal())
                : new ValueTypeEncoder<decimal>(name, static (v, _) => v.AsDecimal()),

            // Timestamp (naïve) → CLR DateTime with Kind=Unspecified. Parquet.Net
            // serialises it as a logical TIMESTAMP with isAdjustedToUTC=false,
            // matching the Heliosoph.DatumV semantics that a bare Timestamp does
            // not carry a timezone.
            DataKind.Timestamp => nullable
                ? new NullableValueTypeEncoder<DateTime>(name, static (v, _) => v.AsTimestamp())
                : new ValueTypeEncoder<DateTime>(name, static (v, _) => v.AsTimestamp()),

            // TimestampTz → UTC-normalised DateTime. Parquet.Net 5.x dropped
            // DateTimeOffset writer support ("numerous ambiguity issues"); the
            // canonical path now is DateTime with the value already in UTC.
            // The original wall-clock offset is not preserved on disk — Parquet
            // TIMESTAMP isAdjustedToUTC=true stores the instant only — so
            // round-tripping reads back a DateTime, not a DateTimeOffset. The
            // datumv.kind tag disambiguates from a naïve Timestamp on re-import
            // — open_parquet retags the column as TimestampTz with a UTC
            // offset so type-aware SQL functions see the expected kind.
            DataKind.TimestampTz => nullable
                ? new NullableValueTypeEncoder<DateTime>(name, static (v, _) => v.AsTimestampTz().UtcDateTime,
                    datumKind: "TimestampTz", datumFormat: "passthrough")
                : new ValueTypeEncoder<DateTime>(name, static (v, _) => v.AsTimestampTz().UtcDateTime,
                    datumKind: "TimestampTz", datumFormat: "passthrough"),

            // Date → Parquet INT32 logical Date. Parquet.Net lifts that back
            // to a DateTime on read; the datumv.kind tag tells open_parquet to
            // narrow it to a DateOnly-backed Date so the SQL kind survives the
            // round trip.
            DataKind.Date => nullable
                ? new NullableValueTypeEncoder<DateOnly>(name, static (v, _) => v.AsDate(),
                    datumKind: "Date", datumFormat: "passthrough")
                : new ValueTypeEncoder<DateOnly>(name, static (v, _) => v.AsDate(),
                    datumKind: "Date", datumFormat: "passthrough"),

            DataKind.Time => nullable
                ? new NullableValueTypeEncoder<TimeOnly>(name, static (v, _) => v.AsTime())
                : new ValueTypeEncoder<TimeOnly>(name, static (v, _) => v.AsTime()),

            DataKind.Uuid => nullable
                ? new NullableValueTypeEncoder<Guid>(name, static (v, _) => v.AsUuid())
                : new ValueTypeEncoder<Guid>(name, static (v, _) => v.AsUuid()),

            // Strings are inherently reference-typed and always treated as nullable
            // by Parquet.Net's DataField<string>. SQL nullability is honoured at
            // append time — a NULL DataValue emits a CLR null into the column.
            // Long strings (and any string written via INSERT ... FROM SCAN of a
            // .datum-backed table) commonly land in a .datum-blob sidecar rather
            // than the row arena; the extractor closes over the per-export
            // SidecarRegistry so AsString can resolve the storeId. Non-static
            // lambda by design — captures the registry from the enclosing
            // factory call.
            DataKind.String => new ReferenceTypeEncoder<string>(
                name, (v, store) => v.AsString(store, sidecarRegistry)),

            // Typed-media kinds (Image / Audio / Video / Mesh / PointCloud)
            // flow into Parquet as LIST<UInt8> (an array column of bytes per
            // row) rather than the raw-BYTE_ARRAY shape Parquet.Net's
            // DataField<byte[]> emits. Two reasons:
            //   * The Heliosoph.DatumV reader (open_parquet) decodes BYTE_ARRAY
            //     columns as scalar UInt8 (one byte per row), which is wrong
            //     for media payloads — the array shape round-trips cleanly
            //     through the existing ParquetColumnReader.ReadArrayColumn
            //     path with no reader-side changes.
            //   * The meta surfaces is_array=true, which matches the user
            //     intuition of "image = array of bytes" and lets downstream
            //     SQL treat the column with the usual array accessors.
            // This is still the Inline media disposition; sidecar mode is
            // reserved for a follow-up that adds the Directory target. Each
            // extractor closes over the per-export SidecarRegistry so
            // sidecar-backed values (the common case for ingested datasets —
            // bytes live in .datum-blob files, not in the row arena) resolve
            // their storeId at append time.
            //
            // datumv.kind / datumv.format strings: written into the Parquet
            // column-chunk metadata via ByteArrayListEncoder so open_parquet
            // can re-route the column back to its typed DataKind on read
            // without the user having to wrap with the matching `_from_X`
            // importer. External tools see opaque KV pairs and ignore them.
            DataKind.Image => new ByteArrayListEncoder(
                name, nullable, (v, store) => v.AsImage(store, sidecarRegistry),
                datumKind: "Image", datumFormat: "passthrough"),

            DataKind.Audio => new ByteArrayListEncoder(
                name, nullable, (v, store) => v.AsAudio(store, sidecarRegistry),
                datumKind: "Audio", datumFormat: "passthrough"),

            DataKind.Video => new ByteArrayListEncoder(
                name, nullable, (v, store) => v.AsVideo(store, sidecarRegistry),
                datumKind: "Video", datumFormat: "passthrough"),

            // Mesh / PointCloud go out as universal interchange formats — .glb
            // and binary PLY respectively — so the resulting Parquet column is
            // immediately usable in Blender / MeshLab / CloudCompare / Open3D
            // / Three.js / web glTF viewers without an intermediate decode
            // step. Round-tripping back to a typed Mesh / PointCloud column
            // goes through the inverse `mesh_from_gltf` / `pointcloud_from_ply`
            // scalar functions, which open_parquet calls automatically when
            // it sees the datumv.kind metadata.
            DataKind.Mesh => new ByteArrayListEncoder(
                name, nullable, (v, store) => GltfExporter.Export(
                    v.AsMesh(store, sidecarRegistry), generator: "Heliosoph.DatumV"),
                datumKind: "Mesh", datumFormat: "gltf"),

            DataKind.PointCloud => new ByteArrayListEncoder(
                name, nullable, (v, store) => PlyExporter.Export(
                    v.AsPointCloud(store, sidecarRegistry), generator: "Heliosoph.DatumV"),
                datumKind: "PointCloud", datumFormat: "ply"),

            // Json columns export as a UTF-8 string column carrying the
            // canonical JSON text. Pandas / DuckDB / Spark / Polars read
            // the column as a plain string and pretty-print it directly;
            // the datumv.kind=Json tag plus datumv.format=text tells
            // open_parquet to re-encode the text back to CBOR so the
            // engine's DataKind.Json contract (bytes are canonical CBOR)
            // survives the round trip. The encoder closes over the per-
            // export SidecarRegistry so JSON values whose CBOR bytes live
            // in a .datum-blob sidecar resolve their storeId at append
            // time, same as the other typed-media paths.
            DataKind.Json => new ReferenceTypeEncoder<string>(
                name,
                (v, store) => Heliosoph.DatumV.Functions.Json.CborJsonCodec.DecodeToJsonText(
                    v.AsByteSpan(store, sidecarRegistry)),
                datumKind: "Json",
                datumFormat: ParquetDatumvMetadata.FormatJsonText),

            // STRUCT column → child-per-field encoder bundle plus a Parquet
            // StructField on the schema. v1 supports one level of nesting:
            // children must be primitive (DataField). The struct encoder
            // delegates Append / Flush to its children and surfaces their
            // aggregated buffered byte count so the sink's flush trigger
            // sees struct-heavy rows as a single load.
            DataKind.Struct => new StructColumnEncoder(
                name,
                column.Fields ?? throw new ExportPlanException(
                    $"COPY TO parquet: column '{column.Name}' has kind Struct but no field metadata. " +
                    "The source query's resolved schema didn't carry struct children — re-check the " +
                    "projection expression or use a CAST to a struct type with named fields."),
                sidecarRegistry),

            // Drawing values are a procedural recipe (DrawingPayload tree),
            // not bytes — they only become a byte sequence after passing
            // through the `render(drawing, size)` SQL function. Reject at
            // plan time and tell the caller to rasterise first; making the
            // conversion implicit would silently swallow the size choice
            // and produce surprising rasterisations.
            DataKind.Drawing => throw new ExportPlanException(
                $"COPY TO parquet: column '{column.Name}' has kind Drawing, which is a procedural " +
                "recipe rather than a byte payload. Rasterise it explicitly in the source query " +
                "with `render(drawing, point2d(width, height))` to produce an Image column, then " +
                "export that."),

            _ => throw new ExportPlanException(
                $"COPY TO parquet: column '{column.Name}' has kind {column.Kind}, which the v1 " +
                "Parquet sink does not yet encode. (Supported v1 kinds: Boolean, Int8/16/32/64, " +
                "UInt8/16/32/64, Float32/64, Decimal, Timestamp, TimestampTz, Date, Time, Uuid, " +
                "String, Image, Audio, Video, Mesh, PointCloud.)"),
        };
    }

    /// <summary>
    /// Builds the <c>LIST&lt;T&gt;</c> encoder for an <c>Array&lt;T&gt;</c>
    /// column. Mirrors <see cref="ByteArrayListEncoder"/> for non-byte
    /// element types: each row's typed array is appended via
    /// <see cref="DataValue.AsArraySpan{T}"/> (or per-kind reference-array
    /// accessor); the column-chunk shape on disk is
    /// <c>(values: T[], repetitionLevels: int[])</c>.
    /// </summary>
    /// <remarks>
    /// SQL NULL rows throw at append time, same as the typed-media list
    /// encoder. Per-element nullability is not surfaced — Heliosoph.DatumV
    /// typed arrays don't carry it on the SQL side.
    /// </remarks>
    private static ParquetColumnEncoder CreateArrayEncoder(
        ColumnInfo column, string name, bool nullable, SidecarRegistry? sidecarRegistry)
    {
        // UInt8 + IsArray is the byte-array shape; route it through the
        // existing ByteArrayListEncoder so the on-disk LIST<UInt8> matches
        // the typed-media columns (and round-trips through the same reader
        // path). No datumv.* metadata — a plain byte array stays a plain
        // byte array.
        if (column.Kind == DataKind.UInt8)
        {
            return new ByteArrayListEncoder(
                name, nullable, static (v, store) => v.AsUInt8Array(store));
        }

        return column.Kind switch
        {
            DataKind.Boolean => new PrimitiveArrayListEncoder<bool>(
                name, nullable, static (v, store) => v.AsArraySpan<bool>(store).ToArray()),
            DataKind.Int8 => new PrimitiveArrayListEncoder<sbyte>(
                name, nullable, static (v, store) => v.AsArraySpan<sbyte>(store).ToArray()),
            DataKind.Int16 => new PrimitiveArrayListEncoder<short>(
                name, nullable, static (v, store) => v.AsArraySpan<short>(store).ToArray()),
            DataKind.UInt16 => new PrimitiveArrayListEncoder<ushort>(
                name, nullable, static (v, store) => v.AsArraySpan<ushort>(store).ToArray()),
            DataKind.Int32 => new PrimitiveArrayListEncoder<int>(
                name, nullable, static (v, store) => v.AsArraySpan<int>(store).ToArray()),
            DataKind.UInt32 => new PrimitiveArrayListEncoder<uint>(
                name, nullable, static (v, store) => v.AsArraySpan<uint>(store).ToArray()),
            DataKind.Int64 => new PrimitiveArrayListEncoder<long>(
                name, nullable, static (v, store) => v.AsArraySpan<long>(store).ToArray()),
            DataKind.UInt64 => new PrimitiveArrayListEncoder<ulong>(
                name, nullable, static (v, store) => v.AsArraySpan<ulong>(store).ToArray()),
            DataKind.Float32 => new PrimitiveArrayListEncoder<float>(
                name, nullable, static (v, store) => v.AsArraySpan<float>(store).ToArray()),
            DataKind.Float64 => new PrimitiveArrayListEncoder<double>(
                name, nullable, static (v, store) => v.AsArraySpan<double>(store).ToArray()),
            DataKind.String => new StringArrayListEncoder(name, nullable),

            DataKind.Struct => new StructArrayListEncoder(
                name,
                column.Fields ?? throw new ExportPlanException(
                    $"COPY TO parquet: column '{column.Name}' is Array<Struct> but no field metadata " +
                    "was attached to the projection. Make sure the source query exposes an array " +
                    "literal with struct elements (e.g. `[{ a: 1, b: 'x' }]`)."),
                sidecarRegistry),

            _ => throw new ExportPlanException(
                $"COPY TO parquet: column '{column.Name}' is Array<{column.Kind}>, which the v1 " +
                "Parquet sink does not yet encode. (Supported array element kinds: Boolean, " +
                "Int8/16/32/64, UInt8/16/32/64, Float32/64, String.)"),
        };
    }
}

/// <summary>
/// Non-nullable value-type column encoder. Buffers values directly in a
/// <c>List&lt;T&gt;</c>; flushes by materialising a typed array.
/// </summary>
internal sealed class ValueTypeEncoder<T> : ParquetColumnEncoder where T : struct
{
    private readonly DataField<T> _field;
    private readonly List<T> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;
    private readonly Dictionary<string, string>? _columnMetadata;

    public ValueTypeEncoder(
        string name,
        Func<DataValue, IValueStore, T> extract,
        string? datumKind = null,
        string? datumFormat = null)
    {
        _field = new DataField<T>(name);
        _extract = extract;
        _columnMetadata = BuildDatumvMetadata(datumKind, datumFormat);
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override IReadOnlyDictionary<string, string>? ColumnMetadata => _columnMetadata;

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            // The planner gates non-nullable columns; a runtime null here means the
            // source query produced one anyway. Surface a clear runtime error.
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' is non-nullable but the source " +
                "query produced a NULL value.");
        }
        _buffer.Add(_extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T[] arr = _buffer.ToArray();
        _buffer.Clear();
        DataColumn dataColumn = new(_field, arr);
        if (_columnMetadata is not null)
        {
            await rg.WriteColumnAsync(dataColumn, _columnMetadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await rg.WriteColumnAsync(dataColumn, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Nullable value-type column encoder. Buffers values as <c>T?</c>, emits
/// the <c>T?[]</c> array Parquet.Net wires up for nullable primitive
/// columns.
/// </summary>
internal sealed class NullableValueTypeEncoder<T> : ParquetColumnEncoder where T : struct
{
    private readonly DataField<T?> _field;
    private readonly List<T?> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;
    private readonly Dictionary<string, string>? _columnMetadata;

    public NullableValueTypeEncoder(
        string name,
        Func<DataValue, IValueStore, T> extract,
        string? datumKind = null,
        string? datumFormat = null)
    {
        _field = new DataField<T?>(name);
        _extract = extract;
        _columnMetadata = BuildDatumvMetadata(datumKind, datumFormat);
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override IReadOnlyDictionary<string, string>? ColumnMetadata => _columnMetadata;

    public override void Append(DataValue value, IValueStore store)
    {
        _buffer.Add(value.IsNull ? null : _extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T?[] arr = _buffer.ToArray();
        _buffer.Clear();
        DataColumn dataColumn = new(_field, arr);
        if (_columnMetadata is not null)
        {
            await rg.WriteColumnAsync(dataColumn, _columnMetadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await rg.WriteColumnAsync(dataColumn, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Reference-type column encoder (string, byte[]). Reference types are
/// inherently nullable in CLR; a SQL NULL maps to a CLR null in the buffer.
/// </summary>
internal sealed class ReferenceTypeEncoder<T> : ParquetColumnEncoder where T : class
{
    private readonly DataField<T> _field;
    private readonly List<T?> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;
    private readonly Dictionary<string, string>? _columnMetadata;

    public ReferenceTypeEncoder(
        string name,
        Func<DataValue, IValueStore, T> extract,
        string? datumKind = null,
        string? datumFormat = null)
    {
        _field = new DataField<T>(name);
        _extract = extract;
        _columnMetadata = BuildDatumvMetadata(datumKind, datumFormat);
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override IReadOnlyDictionary<string, string>? ColumnMetadata => _columnMetadata;

    public override void Append(DataValue value, IValueStore store)
    {
        _buffer.Add(value.IsNull ? null : _extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T?[] arr = _buffer.ToArray();
        _buffer.Clear();
        DataColumn dataColumn = new(_field, arr);
        if (_columnMetadata is not null)
        {
            await rg.WriteColumnAsync(dataColumn, _columnMetadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await rg.WriteColumnAsync(dataColumn, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Byte-array column encoder that writes as a Parquet
/// <c>LIST&lt;UInt8&gt;</c> (an array column whose values are sequences
/// of bytes), not as raw <c>BYTE_ARRAY</c>. Used for typed-media columns
/// whose payload is a byte sequence per row — Image today; Audio /
/// Video / Mesh / PointCloud / Json will follow the same pattern.
/// </summary>
/// <remarks>
/// Parquet's array shape is encoded as a flat values array plus
/// repetition / definition level streams:
/// <list type="bullet">
///   <item><description>repetition level 0 marks the first element of
///   a new row's list; level 1 continues the current row's list.</description></item>
///   <item><description>For nullable fields, definition level 0 means
///   the row's list is NULL, level 2 means an element is present.
///   Empty lists are not produced by this encoder in practice — image
///   bytes are never zero-length; the SQL NULL path covers the
///   missing-value case.</description></item>
///   <item><description>For non-nullable fields, definition levels are
///   omitted; every emitted element is implicitly present.</description></item>
/// </list>
/// </remarks>
internal sealed class ByteArrayListEncoder : ParquetColumnEncoder
{
    // Honours the source column's SQL nullability — `nullable: true` produces
    // a Parquet `optional` LIST<UInt8> and the encoder emits a definition-
    // level stream so NULL rows survive the round trip (the writer maps a
    // SQL NULL row to a single (rep=0, def=0) marker with no value
    // contribution). `nullable: false` keeps the original repetition-only
    // path and throws on NULL at append time, matching the strict shape
    // ingestion fixtures expect.
    private readonly DataField _field;
    private readonly bool _nullable;
    private readonly List<byte[]?> _buffer = new();
    private readonly Func<DataValue, IValueStore, byte[]> _extract;
    // Stored as the mutable Dictionary<string,string> Parquet.Net's three-arg
    // WriteColumnAsync expects. Surfaced through ColumnMetadata as an
    // IReadOnlyDictionary so the contract stays read-only externally.
    private readonly Dictionary<string, string>? _columnMetadata;
    private long _bufferedBytes;

    public ByteArrayListEncoder(
        string name,
        bool nullable,
        Func<DataValue, IValueStore, byte[]> extract,
        string? datumKind = null,
        string? datumFormat = null)
    {
        _nullable = nullable;
        _field = new DataField(name, typeof(byte), isNullable: nullable, isArray: true);
        _extract = extract;

        // Per-column-chunk metadata. Only populated when both `datumKind` and
        // `datumFormat` are supplied — the typed-media factory paths set
        // both; raw byte-array consumers (if any future caller uses this
        // encoder for a non-typed-media column) pass null and produce no
        // metadata, leaving the file readable by Heliosoph.DatumV as plain
        // UInt8[] (the pre-metadata behaviour).
        _columnMetadata = BuildDatumvMetadata(datumKind, datumFormat);
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override long BufferedBytes => _bufferedBytes;
    public override IReadOnlyDictionary<string, string>? ColumnMetadata => _columnMetadata;

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            if (!_nullable)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: column '{_field.Name}' is declared non-nullable but the " +
                    "source query produced a NULL value. Filter NULLs out upstream or mark the " +
                    "column nullable.");
            }
            _buffer.Add(null);
            return;
        }
        byte[] payload = _extract(value, store);
        _buffer.Add(payload);
        _bufferedBytes += payload.Length;
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;

        // Accumulate in long so a few hundred KB × thousands of rows can't
        // silently overflow the Int32 we'd later hand Parquet.Net as an
        // array length. Real meshes ran ~200 KB each; 50,000-row groups
        // were tripping the writer's RleBitpackedHybridEncoder with a
        // negative minimumLength. The byte-budget flush in the sink keeps
        // us below the cap for normal workloads; this is a defensive
        // assertion against an arithmetic boundary.
        long totalBytes = 0L;
        long totalLevels = 0L;
        foreach (byte[]? row in _buffer)
        {
            if (row is null)
            {
                // Nullable mode only — see Append guard. NULL rows contribute
                // one position to the level streams (rep=0, def=0) but zero
                // bytes to the value buffer.
                totalLevels++;
                continue;
            }
            totalBytes += row.Length;
            totalLevels += row.Length == 0 ? 1 : row.Length;
        }
        if (totalBytes > int.MaxValue || totalLevels > int.MaxValue)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' accumulated {totalBytes:N0} bytes " +
                $"across {_buffer.Count:N0} rows in a single row group, exceeding the " +
                $"~2 GB Parquet writer limit. Use a smaller ROW_GROUP_SIZE (or rely on the " +
                "byte-budget flush) for typed-media columns with large blobs.");
        }

        byte[] values = new byte[totalBytes];
        int[] repetitionLevels = new int[totalLevels];
        int[]? definitionLevels = _nullable ? new int[totalLevels] : null;
        int maxDef = _field.MaxDefinitionLevel;

        int valueIdx = 0;
        int levelIdx = 0;
        foreach (byte[]? row in _buffer)
        {
            if (row is null)
            {
                // NULL list row: one (rep=0, def=0) marker; no bytes.
                repetitionLevels[levelIdx] = 0;
                definitionLevels![levelIdx] = 0;
                levelIdx++;
                continue;
            }
            if (row.Length == 0)
            {
                // Empty present list: single marker with def at one below
                // max (LIST present, no element present). The non-nullable
                // path keeps the legacy rep=0 marker for backwards
                // compatibility with fixtures that never hit this branch.
                repetitionLevels[levelIdx] = 0;
                if (definitionLevels is not null) definitionLevels[levelIdx] = maxDef - 1;
                levelIdx++;
                continue;
            }
            for (int i = 0; i < row.Length; i++)
            {
                values[valueIdx++] = row[i];
                repetitionLevels[levelIdx] = i == 0 ? 0 : 1;
                if (definitionLevels is not null) definitionLevels[levelIdx] = maxDef;
                levelIdx++;
            }
        }
        _buffer.Clear();
        _bufferedBytes = 0L;

        // DataColumn's 4-arg positional ctor is (field, data,
        // definitionLevels, repetitionLevels) — use named args so the
        // parameter order can't silently slip.
        DataColumn dataColumn = definitionLevels is not null
            ? new DataColumn(_field, values,
                definitionLevels: definitionLevels, repetitionLevels: repetitionLevels)
            : new DataColumn(_field, values, repetitionLevels);
        if (_columnMetadata is not null)
        {
            // Three-arg overload: same KV map written on every row group's
            // column chunk so the reader sees consistent metadata regardless
            // of which row group it inspects.
            await rg.WriteColumnAsync(dataColumn, _columnMetadata, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await rg.WriteColumnAsync(dataColumn, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Primitive-element <c>Array&lt;T&gt;</c> column encoder. Writes a
/// Parquet <c>LIST&lt;T&gt;</c> (a flat values array plus repetition
/// levels) — the same shape <see cref="ByteArrayListEncoder"/> uses for
/// typed-media columns, but with an unmanaged element type other than
/// byte. Used for Int32 / Int64 / Float32 / Float64 / Boolean / etc.
/// arrays.
/// </summary>
/// <remarks>
/// Mirrors the <see cref="ByteArrayListEncoder"/> contract:
/// non-nullable on disk (SQL NULL rows throw at append time); buffered
/// byte accounting drives the sink's byte-budget flush trigger.
/// Per-element nullability is not surfaced — typed arrays on the
/// Heliosoph.DatumV side don't carry it.
/// </remarks>
internal sealed class PrimitiveArrayListEncoder<T> : ParquetColumnEncoder
    where T : unmanaged
{
    private readonly DataField _field;
    private readonly List<T[]> _buffer = new();
    private readonly Func<DataValue, IValueStore, T[]> _extract;
    private readonly int _elementSize;
    private long _bufferedBytes;

    public PrimitiveArrayListEncoder(
        string name,
        bool nullable,
        Func<DataValue, IValueStore, T[]> extract)
    {
        // Force non-nullable on disk regardless of the source column's SQL
        // nullability — see ByteArrayListEncoder note.
        _ = nullable;
        _field = new DataField(name, typeof(T), isNullable: false, isArray: true);
        _extract = extract;
        unsafe { _elementSize = sizeof(T); }
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override long BufferedBytes => _bufferedBytes;

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' is Array<{typeof(T).Name}> and the source " +
                "query produced a NULL row. Nullable array columns are not yet supported by the v1 " +
                "Parquet sink; filter NULLs out of the source query first.");
        }
        T[] payload = _extract(value, store);
        _buffer.Add(payload);
        _bufferedBytes += (long)payload.Length * _elementSize;
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;

        long totalElements = 0L;
        long totalLevels = 0L;
        foreach (T[] row in _buffer)
        {
            totalElements += row.Length;
            totalLevels += row.Length == 0 ? 1 : row.Length;
        }
        if (totalElements > int.MaxValue || totalLevels > int.MaxValue)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' accumulated {totalElements:N0} elements " +
                $"across {_buffer.Count:N0} rows in a single row group, exceeding the Parquet " +
                "writer's Int32 array-length limit. Use a smaller ROW_GROUP_SIZE for very wide " +
                "array columns.");
        }

        T[] values = new T[totalElements];
        int[] repetitionLevels = new int[totalLevels];

        int valueIdx = 0;
        int levelIdx = 0;
        foreach (T[] row in _buffer)
        {
            if (row.Length == 0)
            {
                // Zero-length list marker — single rep=0 entry, no value bytes.
                repetitionLevels[levelIdx++] = 0;
                continue;
            }
            for (int i = 0; i < row.Length; i++)
            {
                values[valueIdx++] = row[i];
                repetitionLevels[levelIdx++] = i == 0 ? 0 : 1;
            }
        }
        _buffer.Clear();
        _bufferedBytes = 0L;

        await rg.WriteColumnAsync(new DataColumn(_field, values, repetitionLevels), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// <c>Array&lt;String&gt;</c> column encoder. Writes a Parquet
/// <c>LIST&lt;String&gt;</c> via a flat <c>string[]</c> values array
/// plus repetition levels — same shape as
/// <see cref="PrimitiveArrayListEncoder{T}"/> but with a reference
/// element type so <see cref="DataValue.AsStringArray"/> resolves the
/// per-element bytes through the supplied store.
/// </summary>
internal sealed class StringArrayListEncoder : ParquetColumnEncoder
{
    private readonly DataField _field;
    private readonly List<string[]> _buffer = new();
    private long _bufferedBytes;

    public StringArrayListEncoder(string name, bool nullable)
    {
        _ = nullable;
        _field = new DataField(name, typeof(string), isNullable: false, isArray: true);
    }

    public override Field Field => _field;
    public override int Count => _buffer.Count;
    public override long BufferedBytes => _bufferedBytes;

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' is Array<String> and the source " +
                "query produced a NULL row. Nullable array columns are not yet supported by the v1 " +
                "Parquet sink; filter NULLs out of the source query first.");
        }
        string[] payload = value.AsStringArray(store);
        _buffer.Add(payload);
        long rowBytes = 0;
        for (int i = 0; i < payload.Length; i++) rowBytes += payload[i]?.Length ?? 0;
        _bufferedBytes += rowBytes;
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;

        long totalElements = 0L;
        long totalLevels = 0L;
        foreach (string[] row in _buffer)
        {
            totalElements += row.Length;
            totalLevels += row.Length == 0 ? 1 : row.Length;
        }
        if (totalElements > int.MaxValue || totalLevels > int.MaxValue)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' accumulated {totalElements:N0} elements " +
                $"across {_buffer.Count:N0} rows in a single row group, exceeding the Parquet " +
                "writer's Int32 array-length limit. Use a smaller ROW_GROUP_SIZE for very wide " +
                "array columns.");
        }

        string[] values = new string[totalElements];
        int[] repetitionLevels = new int[totalLevels];

        int valueIdx = 0;
        int levelIdx = 0;
        foreach (string[] row in _buffer)
        {
            if (row.Length == 0)
            {
                repetitionLevels[levelIdx++] = 0;
                continue;
            }
            for (int i = 0; i < row.Length; i++)
            {
                values[valueIdx++] = row[i] ?? string.Empty;
                repetitionLevels[levelIdx++] = i == 0 ? 0 : 1;
            }
        }
        _buffer.Clear();
        _bufferedBytes = 0L;

        await rg.WriteColumnAsync(new DataColumn(_field, values, repetitionLevels), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Top-level <c>STRUCT</c> column encoder. Owns one child
/// <see cref="ParquetColumnEncoder"/> per field; <see cref="Append"/>
/// unpacks the incoming struct value via
/// <see cref="DataValue.AsStruct(IValueStore)"/> and delegates each field
/// to its child encoder. On flush each child writes its own
/// <see cref="DataColumn"/> — Parquet.Net groups them under the schema's
/// <see cref="StructField"/> automatically.
/// </summary>
/// <remarks>
/// v1 supports primitive scalar children and 1-D <c>Array&lt;T&gt;</c>
/// children (where T is Boolean, Int8/16/32/64, UInt8/16/32/64,
/// Float32/64, Decimal, Timestamp, Date, Time, Uuid, or String). Array
/// children are wired through a real Parquet <see cref="ListField"/>
/// inside the wrapping <see cref="StructField"/>, with per-element
/// repetition / definition levels emitted at flush time; the scalar
/// children write through their normal primitive encoders without level
/// streams. Nested STRUCT inside STRUCT is still rejected at plan time.
/// Per-row struct NULLs, empty list children, and per-element list nulls
/// throw at append time.
/// </remarks>
internal sealed class StructColumnEncoder : ParquetColumnEncoder
{
    private readonly StructField _field;
    private readonly StructChildHandler[] _children;
    private int _bufferedRows;

    public StructColumnEncoder(
        string name,
        IReadOnlyList<ColumnInfo> childInfos,
        SidecarRegistry? sidecarRegistry)
    {
        if (childInfos.Count == 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: STRUCT column '{name}' has zero fields. " +
                "A struct must declare at least one field.");
        }

        _children = new StructChildHandler[childInfos.Count];
        Field[] childFields = new Field[childInfos.Count];
        for (int i = 0; i < childInfos.Count; i++)
        {
            _children[i] = StructChildHandler.Create(name, childInfos[i], sidecarRegistry);
            childFields[i] = _children[i].Field;
        }
        _field = new StructField(name, childFields);
    }

    public override Field Field => _field;
    public override int Count => _bufferedRows;

    public override long BufferedBytes
    {
        get
        {
            long total = 0L;
            for (int i = 0; i < _children.Length; i++) total += _children[i].BufferedBytes;
            return total;
        }
    }

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: STRUCT column '{_field.Name}' is non-nullable in the v1 sink " +
                "and the source query produced a NULL value. Filter NULL rows out of the source " +
                "query or coalesce them to a placeholder struct literal.");
        }
        if (value.Kind != DataKind.Struct)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: STRUCT column '{_field.Name}' expected a Struct value at " +
                $"runtime; got {value.Kind}. The source projection's runtime kind diverged from " +
                "the planner's declared schema.");
        }

        DataValue[] fields = value.AsStruct(store);
        if (fields.Length != _children.Length)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: STRUCT column '{_field.Name}' value carries {fields.Length} " +
                $"fields; schema declares {_children.Length}. Source struct shape must match the " +
                "planner-resolved schema.");
        }

        for (int i = 0; i < _children.Length; i++)
        {
            _children[i].Append(fields[i], store);
        }
        _bufferedRows++;
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_bufferedRows == 0) return;
        for (int i = 0; i < _children.Length; i++)
        {
            await _children[i].FlushAsync(rg, cancellationToken).ConfigureAwait(false);
        }
        _bufferedRows = 0;
    }

    /// <summary>
    /// Common surface for the per-field handlers a <see cref="StructColumnEncoder"/>
    /// owns. Either a <see cref="ScalarStructChild"/> (a plain primitive
    /// encoder writing one value per outer row, no level streams) or a
    /// <see cref="ListStructChild{T}"/> / <see cref="StringListStructChild"/>
    /// (a Parquet <see cref="ListField"/> writing per-element values plus
    /// the repetition / definition levels that mark each row's list
    /// boundary).
    /// </summary>
    private abstract class StructChildHandler
    {
        public abstract Field Field { get; }
        public abstract long BufferedBytes { get; }
        public abstract void Append(DataValue value, IValueStore store);
        public abstract Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken);

        public static StructChildHandler Create(
            string parentName, ColumnInfo info, SidecarRegistry? sidecarRegistry)
        {
            if (info.Kind == DataKind.Struct)
            {
                throw new ExportPlanException(
                    $"COPY TO parquet: STRUCT column '{parentName}' child '{info.Name}' is itself a " +
                    "STRUCT. Nested STRUCT inside STRUCT is not supported by the v1 Parquet sink — " +
                    "flatten the source projection or split into separate top-level columns.");
            }
            if (info.IsArray)
            {
                return CreateListChild(parentName, info, sidecarRegistry);
            }
            return new ScalarStructChild(ParquetColumnEncoder.Create(info, sidecarRegistry));
        }

        private static StructChildHandler CreateListChild(
            string parentName, ColumnInfo info, SidecarRegistry? sidecarRegistry)
        {
            string name = info.Name;
            return info.Kind switch
            {
                DataKind.Boolean => new ListStructChild<bool>(
                    name, static (v, store) => v.AsArraySpan<bool>(store).ToArray(), elementSize: 1),
                DataKind.Int8 => new ListStructChild<sbyte>(
                    name, static (v, store) => v.AsArraySpan<sbyte>(store).ToArray(), elementSize: 1),
                DataKind.Int16 => new ListStructChild<short>(
                    name, static (v, store) => v.AsArraySpan<short>(store).ToArray(), elementSize: 2),
                DataKind.Int32 => new ListStructChild<int>(
                    name, static (v, store) => v.AsArraySpan<int>(store).ToArray(), elementSize: 4),
                DataKind.Int64 => new ListStructChild<long>(
                    name, static (v, store) => v.AsArraySpan<long>(store).ToArray(), elementSize: 8),
                DataKind.UInt8 => new ListStructChild<byte>(
                    name, static (v, store) => v.AsUInt8Array(store), elementSize: 1),
                DataKind.UInt16 => new ListStructChild<ushort>(
                    name, static (v, store) => v.AsArraySpan<ushort>(store).ToArray(), elementSize: 2),
                DataKind.UInt32 => new ListStructChild<uint>(
                    name, static (v, store) => v.AsArraySpan<uint>(store).ToArray(), elementSize: 4),
                DataKind.UInt64 => new ListStructChild<ulong>(
                    name, static (v, store) => v.AsArraySpan<ulong>(store).ToArray(), elementSize: 8),
                DataKind.Float32 => new ListStructChild<float>(
                    name, static (v, store) => v.AsArraySpan<float>(store).ToArray(), elementSize: 4),
                DataKind.Float64 => new ListStructChild<double>(
                    name, static (v, store) => v.AsArraySpan<double>(store).ToArray(), elementSize: 8),
                DataKind.String => new StringListStructChild(name),
                _ => throw new ExportPlanException(
                    $"COPY TO parquet: STRUCT column '{parentName}' child '{name}' is " +
                    $"Array<{info.Kind}>, which is not yet supported as a STRUCT child. (Supported " +
                    "v1: Boolean, Int8/16/32/64, UInt8/16/32/64, Float32/64, String.)"),
            };
        }
    }

    /// <summary>
    /// Scalar primitive child: delegates Append / Flush to a normal
    /// <see cref="ParquetColumnEncoder"/>. The encoder writes a plain
    /// <see cref="DataColumn"/> (no level streams) — one entry per outer
    /// row, which matches Parquet's "required field inside required
    /// STRUCT" layer count.
    /// </summary>
    private sealed class ScalarStructChild : StructChildHandler
    {
        private readonly ParquetColumnEncoder _encoder;

        public ScalarStructChild(ParquetColumnEncoder encoder) { _encoder = encoder; }

        public override Field Field => _encoder.Field;
        public override long BufferedBytes => _encoder.BufferedBytes;
        public override void Append(DataValue value, IValueStore store) => _encoder.Append(value, store);
        public override Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken ct)
            => _encoder.FlushAsync(rg, ct);
    }

    /// <summary>
    /// Primitive-element list child: wires a real
    /// <see cref="ListField"/> around a <see cref="DataField{T}"/> so the
    /// resulting Parquet schema is <c>STRUCT&lt;…, LIST&lt;T&gt;, …&gt;</c>.
    /// Per Append flattens the row's typed array into a per-leaf flat
    /// buffer and stages the per-element repetition level (0 for the
    /// first, max-rep for continuations); Flush emits the
    /// <see cref="DataColumn"/> with the level streams using named args
    /// so the parameter order can't slip.
    /// </summary>
    private sealed class ListStructChild<T> : StructChildHandler where T : unmanaged
    {
        private readonly ListField _listField;
        private readonly DataField<T> _leafField;
        private readonly Func<DataValue, IValueStore, T[]> _extract;
        private readonly List<T> _values = new();
        private readonly List<int> _repLevels = new();
        private readonly int _elementSize;
        private long _bufferedBytes;

        public ListStructChild(string name, Func<DataValue, IValueStore, T[]> extract, int elementSize)
        {
            _leafField = new DataField<T>("element");
            _listField = new ListField(name, _leafField);
            _extract = extract;
            _elementSize = elementSize;
        }

        public override Field Field => _listField;
        public override long BufferedBytes => _bufferedBytes;

        public override void Append(DataValue value, IValueStore store)
        {
            if (value.IsNull)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: LIST child '{_listField.Name}' inside a STRUCT is NULL — " +
                    "nullable list children are not yet supported by the v1 Parquet sink.");
            }
            T[] elements = _extract(value, store);
            if (elements.Length == 0)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: LIST child '{_listField.Name}' inside a STRUCT is empty — " +
                    "empty lists are not yet supported by the v1 Parquet sink.");
            }
            int maxRep = _leafField.MaxRepetitionLevel;
            for (int i = 0; i < elements.Length; i++)
            {
                _values.Add(elements[i]);
                _repLevels.Add(i == 0 ? 0 : maxRep);
            }
            _bufferedBytes += (long)elements.Length * _elementSize;
        }

        public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken ct)
        {
            if (_repLevels.Count == 0) return;
            int[] rep = _repLevels.ToArray();
            int maxDef = _leafField.MaxDefinitionLevel;
            int[] def = new int[rep.Length];
            for (int i = 0; i < def.Length; i++) def[i] = maxDef;
            await rg.WriteColumnAsync(
                new DataColumn(_leafField, _values.ToArray(),
                    definitionLevels: def, repetitionLevels: rep),
                ct)
                .ConfigureAwait(false);
            _values.Clear();
            _repLevels.Clear();
            _bufferedBytes = 0L;
        }
    }

    /// <summary>
    /// <see cref="ListStructChild{T}"/> for string-element lists.
    /// Strings are reference-typed so the value buffer is
    /// <c>List&lt;string?&gt;</c>; the rest of the level / flatten logic
    /// matches the primitive variant.
    /// </summary>
    private sealed class StringListStructChild : StructChildHandler
    {
        private readonly ListField _listField;
        private readonly DataField<string> _leafField;
        private readonly List<string?> _values = new();
        private readonly List<int> _repLevels = new();
        private long _bufferedBytes;

        public StringListStructChild(string name)
        {
            _leafField = new DataField<string>("element");
            _listField = new ListField(name, _leafField);
        }

        public override Field Field => _listField;
        public override long BufferedBytes => _bufferedBytes;

        public override void Append(DataValue value, IValueStore store)
        {
            if (value.IsNull)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: LIST<String> child '{_listField.Name}' inside a STRUCT is " +
                    "NULL — nullable list children are not yet supported by the v1 Parquet sink.");
            }
            string[] elements = value.AsStringArray(store);
            if (elements.Length == 0)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: LIST<String> child '{_listField.Name}' inside a STRUCT is " +
                    "empty — empty lists are not yet supported by the v1 Parquet sink.");
            }
            int maxRep = _leafField.MaxRepetitionLevel;
            for (int i = 0; i < elements.Length; i++)
            {
                _values.Add(elements[i]);
                _repLevels.Add(i == 0 ? 0 : maxRep);
                _bufferedBytes += elements[i]?.Length ?? 0;
            }
        }

        public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken ct)
        {
            if (_repLevels.Count == 0) return;
            int[] rep = _repLevels.ToArray();
            int maxDef = _leafField.MaxDefinitionLevel;
            int[] def = new int[rep.Length];
            for (int i = 0; i < def.Length; i++) def[i] = maxDef;
            await rg.WriteColumnAsync(
                new DataColumn(_leafField, _values.ToArray(),
                    definitionLevels: def, repetitionLevels: rep),
                ct)
                .ConfigureAwait(false);
            _values.Clear();
            _repLevels.Clear();
            _bufferedBytes = 0L;
        }
    }
}

/// <summary>
/// <c>Array&lt;Struct&gt;</c> column encoder. Produces a Parquet
/// <c>LIST&lt;STRUCT&lt;…&gt;&gt;</c> by writing one
/// <see cref="DataColumn"/> per struct field, each sharing the row-level
/// repetition / definition levels that demarcate the per-row list
/// boundaries. The schema declares a
/// <see cref="ListField"/> whose item is a <see cref="StructField"/>
/// over flat <see cref="DataField"/> children.
/// </summary>
/// <remarks>
/// v1 supports one-level nesting: struct children must be primitive
/// (<see cref="DataField"/>); nested struct/list children throw at plan
/// time. Per-row NULL arrays and empty arrays throw at append time —
/// Parquet's definition-level handling for those shapes hasn't converged
/// in the writer surface, and the user-visible payloads we've seen are
/// all non-empty.
/// </remarks>
internal sealed class StructArrayListEncoder : ParquetColumnEncoder
{
    private readonly ListField _field;
    private readonly StructField _itemField;
    private readonly LeafChannel[] _channels;
    private readonly List<int> _elementCountsPerRow = new();
    private int _bufferedRows;

    public StructArrayListEncoder(
        string name,
        IReadOnlyList<ColumnInfo> childInfos,
        SidecarRegistry? sidecarRegistry)
    {
        if (childInfos.Count == 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: Array<Struct> column '{name}' has zero fields. " +
                "A struct must declare at least one field.");
        }

        _channels = new LeafChannel[childInfos.Count];
        DataField[] childFields = new DataField[childInfos.Count];
        for (int i = 0; i < childInfos.Count; i++)
        {
            _channels[i] = LeafChannel.Create(name, childInfos[i], sidecarRegistry);
            childFields[i] = _channels[i].Field;
        }
        _itemField = new StructField("element", childFields);
        _field = new ListField(name, _itemField);
    }

    public override Field Field => _field;
    public override int Count => _bufferedRows;

    public override long BufferedBytes
    {
        get
        {
            long total = 0L;
            for (int i = 0; i < _channels.Length; i++) total += _channels[i].ByteEstimate;
            return total;
        }
    }

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: Array<Struct> column '{_field.Name}' is non-nullable in the v1 " +
                "sink and the source query produced a NULL value. Filter NULL rows out of the " +
                "source query or coalesce them to a placeholder array literal.");
        }
        if (value.Kind != DataKind.Struct || !value.IsArray)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: Array<Struct> column '{_field.Name}' expected an Array<Struct> " +
                $"value at runtime; got {value.Kind}{(value.IsArray ? "[]" : "")}.");
        }

        DataValue[] elements = value.AsStructArray(store);
        if (elements.Length == 0)
        {
            throw new ExportRuntimeException(
                $"COPY TO parquet: Array<Struct> column '{_field.Name}' produced an empty array. " +
                "Empty lists are not yet supported by the v1 Parquet sink — filter them out " +
                "upstream or emit a placeholder element.");
        }

        for (int e = 0; e < elements.Length; e++)
        {
            DataValue element = elements[e];
            if (element.IsNull)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: Array<Struct> column '{_field.Name}' element [{e}] is NULL. " +
                    "Per-element nulls inside an array are not supported.");
            }
            DataValue[] fields = element.AsStruct(store);
            if (fields.Length != _channels.Length)
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: Array<Struct> column '{_field.Name}' element [{e}] has " +
                    $"{fields.Length} fields; schema declares {_channels.Length}.");
            }
            for (int f = 0; f < _channels.Length; f++)
            {
                _channels[f].Append(fields[f], store);
            }
        }

        _elementCountsPerRow.Add(elements.Length);
        _bufferedRows++;
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_bufferedRows == 0) return;

        // Rep levels are shared across all leaves in a LIST<STRUCT<…>> —
        // they describe the list-grouping, which is identical for every
        // leaf at the same nesting depth. Def levels differ per leaf
        // because each leaf's nullability stacks differently above it
        // (a nullable String reaches max def 2, a required Int32 reaches
        // max def 1 even though both share the same outer list path).
        int totalElements = 0;
        for (int r = 0; r < _elementCountsPerRow.Count; r++) totalElements += _elementCountsPerRow[r];

        int maxRepLevel = _channels[0].Field.MaxRepetitionLevel;

        int[] repLevels = new int[totalElements];
        int idx = 0;
        for (int r = 0; r < _elementCountsPerRow.Count; r++)
        {
            int count = _elementCountsPerRow[r];
            for (int e = 0; e < count; e++)
            {
                repLevels[idx] = e == 0 ? 0 : maxRepLevel;
                idx++;
            }
        }

        for (int c = 0; c < _channels.Length; c++)
        {
            // Every element produced by Append is present at the leaf
            // (per-element / per-row NULLs are rejected up front), so
            // every def level equals the leaf's MaxDefinitionLevel.
            int leafMaxDef = _channels[c].Field.MaxDefinitionLevel;
            int[] defLevels = new int[totalElements];
            for (int i = 0; i < totalElements; i++) defLevels[i] = leafMaxDef;

            await _channels[c].WriteColumnAsync(rg, repLevels, defLevels, cancellationToken)
                .ConfigureAwait(false);
        }

        for (int c = 0; c < _channels.Length; c++) _channels[c].Reset();
        _elementCountsPerRow.Clear();
        _bufferedRows = 0;
    }

    /// <summary>
    /// Per-leaf accumulator backing one struct field inside an
    /// <see cref="StructArrayListEncoder"/>. Each channel owns the
    /// <see cref="DataField"/> used in the Parquet schema and stages
    /// element-by-element values until the encoder flushes a row group.
    /// </summary>
    private abstract class LeafChannel
    {
        public abstract DataField Field { get; }
        public abstract long ByteEstimate { get; }
        public abstract void Append(DataValue value, IValueStore store);
        public abstract Task WriteColumnAsync(
            ParquetRowGroupWriter rg, int[] rep, int[] def, CancellationToken ct);
        public abstract void Reset();

        public static LeafChannel Create(
            string parentName, ColumnInfo info, SidecarRegistry? sidecarRegistry)
        {
            if (info.IsArray || info.Kind == DataKind.Struct)
            {
                throw new ExportPlanException(
                    $"COPY TO parquet: Array<Struct> column '{parentName}' child '{info.Name}' " +
                    $"has shape {info.Kind}{(info.IsArray ? "[]" : "")}. Nested arrays / structs " +
                    "inside a STRUCT element are not supported by the v1 Parquet sink.");
            }

            return info.Kind switch
            {
                DataKind.Boolean => new ValueChannel<bool>(info.Name,
                    static (v, _) => v.AsBoolean(), elementSize: 1),
                DataKind.Int8 => new ValueChannel<sbyte>(info.Name,
                    static (v, _) => v.AsInt8(), elementSize: 1),
                DataKind.Int16 => new ValueChannel<short>(info.Name,
                    static (v, _) => v.AsInt16(), elementSize: 2),
                DataKind.Int32 => new ValueChannel<int>(info.Name,
                    static (v, _) => v.AsInt32(), elementSize: 4),
                DataKind.Int64 => new ValueChannel<long>(info.Name,
                    static (v, _) => v.AsInt64(), elementSize: 8),
                DataKind.UInt8 => new ValueChannel<byte>(info.Name,
                    static (v, _) => v.AsUInt8(), elementSize: 1),
                DataKind.UInt16 => new ValueChannel<ushort>(info.Name,
                    static (v, _) => v.AsUInt16(), elementSize: 2),
                DataKind.UInt32 => new ValueChannel<uint>(info.Name,
                    static (v, _) => v.AsUInt32(), elementSize: 4),
                DataKind.UInt64 => new ValueChannel<ulong>(info.Name,
                    static (v, _) => v.AsUInt64(), elementSize: 8),
                DataKind.Float32 => new ValueChannel<float>(info.Name,
                    static (v, _) => v.AsFloat32(), elementSize: 4),
                DataKind.Float64 => new ValueChannel<double>(info.Name,
                    static (v, _) => v.AsFloat64(), elementSize: 8),
                DataKind.Decimal => new ValueChannel<decimal>(info.Name,
                    static (v, _) => v.AsDecimal(), elementSize: 16),
                DataKind.Date => new ValueChannel<DateOnly>(info.Name,
                    static (v, _) => v.AsDate(), elementSize: 4),
                DataKind.Time => new ValueChannel<TimeOnly>(info.Name,
                    static (v, _) => v.AsTime(), elementSize: 8),
                DataKind.Timestamp => new ValueChannel<DateTime>(info.Name,
                    static (v, _) => v.AsTimestamp(), elementSize: 8),
                DataKind.Uuid => new ValueChannel<Guid>(info.Name,
                    static (v, _) => v.AsUuid(), elementSize: 16),
                DataKind.String => new StringChannel(info.Name, sidecarRegistry),
                _ => throw new ExportPlanException(
                    $"COPY TO parquet: Array<Struct> column '{parentName}' child '{info.Name}' has " +
                    $"unsupported kind {info.Kind}. (Supported in v1: Boolean, Int8/16/32/64, " +
                    "UInt8/16/32/64, Float32/64, Decimal, Timestamp, Date, Time, Uuid, String.)"),
            };
        }
    }

    /// <summary>
    /// Value-type leaf channel: buffers raw <c>T</c> values into a
    /// <see cref="List{T}"/> and writes a typed
    /// <see cref="DataColumn"/> at flush.
    /// </summary>
    private sealed class ValueChannel<T> : LeafChannel where T : struct
    {
        private readonly DataField<T> _field;
        private readonly List<T> _values = new();
        private readonly Func<DataValue, IValueStore, T> _extract;
        private readonly int _elementSize;

        public ValueChannel(string name, Func<DataValue, IValueStore, T> extract, int elementSize)
        {
            _field = new DataField<T>(name);
            _extract = extract;
            _elementSize = elementSize;
        }

        public override DataField Field => _field;
        public override long ByteEstimate => (long)_values.Count * _elementSize;

        public override void Append(DataValue value, IValueStore store)
            => _values.Add(_extract(value, store));

        public override async Task WriteColumnAsync(
            ParquetRowGroupWriter rg, int[] rep, int[] def, CancellationToken ct)
        {
            // DataColumn's 4-arg ctor is (field, data, definitionLevels,
            // repetitionLevels) — defs come BEFORE reps in the positional
            // signature. Use named args so the order can't silently slip.
            await rg.WriteColumnAsync(
                new DataColumn(_field, _values.ToArray(),
                    definitionLevels: def, repetitionLevels: rep),
                ct)
                .ConfigureAwait(false);
        }

        public override void Reset() => _values.Clear();
    }

    /// <summary>
    /// String leaf channel: resolves arena / sidecar storage through the
    /// closed-over <see cref="SidecarRegistry"/> at append time, identical
    /// to <see cref="ReferenceTypeEncoder{T}"/>'s String path.
    /// </summary>
    private sealed class StringChannel : LeafChannel
    {
        private readonly DataField<string> _field;
        private readonly List<string?> _values = new();
        private readonly SidecarRegistry? _registry;
        private long _byteEstimate;

        public StringChannel(string name, SidecarRegistry? registry)
        {
            // String leaf is declared nullable (the default) — that's what
            // Parquet.Net accepts for `string?[]` data shapes without a
            // per-call CLR type check. The v1 sink doesn't emit per-row
            // NULLs (those throw in Append), but the writer's contract for
            // non-nullable string columns requires the data array's
            // element type to be exactly `string` (not `string?`), which
            // would require a wider refactor across every leaf channel.
            _field = new DataField<string>(name);
            _registry = registry;
        }

        public override DataField Field => _field;
        public override long ByteEstimate => _byteEstimate;

        public override void Append(DataValue value, IValueStore store)
        {
            string s = value.AsString(store, _registry);
            _values.Add(s);
            _byteEstimate += s.Length;
        }

        public override async Task WriteColumnAsync(
            ParquetRowGroupWriter rg, int[] rep, int[] def, CancellationToken ct)
        {
            await rg.WriteColumnAsync(
                new DataColumn(_field, _values.ToArray(),
                    definitionLevels: def, repetitionLevels: rep),
                ct)
                .ConfigureAwait(false);
        }

        public override void Reset()
        {
            _values.Clear();
            _byteEstimate = 0L;
        }
    }
}
