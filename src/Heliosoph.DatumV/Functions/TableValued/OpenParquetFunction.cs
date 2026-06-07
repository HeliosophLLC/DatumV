using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_parquet(path) → table</c> /
/// <c>open_parquet(path, typed) → table</c>. Opens a Parquet file and
/// yields its rows with one column per leaf field. The output schema is
/// the file's real schema — the validator peeks at plan time so column
/// projections type-check against the actual file's typed columns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plan-time schema peek.</strong> The <c>path</c> argument must
/// be a constant at plan time (literal in source or a
/// <c>$parameter</c> reference that the parameter binder has substituted).
/// The validator opens the file, reads
/// <see cref="ParquetSchema"/>, and produces a real
/// <see cref="Schema"/> per leaf column — so
/// <c>SELECT text FROM open_parquet('foo.parquet')</c> type-checks
/// the projection before any data is read. Non-constant
/// arguments throw <see cref="FunctionArgumentException"/>.
/// </para>
/// <para>
/// <strong>Auto-typed columns.</strong> When <c>typed</c> is true (the
/// default), <c>open_parquet</c> reads each column-chunk's
/// <c>datumv.kind</c> / <c>datumv.format</c> metadata and routes tagged
/// byte-array columns back to their typed <see cref="DataKind"/>
/// (Mesh / PointCloud / Image / Audio / Video). For Mesh and PointCloud
/// the bytes flow through <see cref="GltfImporter"/> /
/// <see cref="PlyImporter"/> per row; for Image / Audio / Video the kind
/// is retagged without a decode (the bytes are already in the universal
/// interchange shape). Files without the metadata or third-party Parquet
/// are unaffected and read as plain <c>UInt8[]</c>. Pass
/// <c>typed = false</c> to bypass the metadata routing and get raw bytes
/// regardless of the tag — useful when piping the column to another
/// export without paying for a round-trip parse.
/// </para>
/// <para>
/// <strong>Supported types (v1).</strong> Booleans, signed and unsigned
/// 8/16/32/64-bit integers, IEEE Float32 / Float64, UTF-8 strings, and
/// byte arrays (raw <c>BYTE_ARRAY</c>). Plus 1-D arrays of any
/// supported primitive (<c>LIST&lt;T&gt;</c>). Nested
/// <c>STRUCT&lt;…&gt;</c>, <c>LIST&lt;LIST&lt;T&gt;&gt;</c>,
/// <c>MAP&lt;K,V&gt;</c>, and rarer logical types (Decimal, Timestamp
/// variants, UUID, JSON) come in a follow-up. Columns with
/// unsupported types throw at validation; recipes can use
/// <c>open_parquet_meta</c> to detect them ahead of time.
/// </para>
/// <para>
/// <strong>Memory.</strong> v1 reads each row group fully into memory
/// before emitting rows. Fine for typical HuggingFace dataset shards
/// (tens to hundreds of MB per row group); larger-than-RAM files will
/// land via the chunked-streaming follow-up that's already on the
/// roadmap.
/// </para>
/// </remarks>
public sealed class OpenParquetFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_parquet";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens a Parquet file and yields its rows with one column per leaf field: " +
        "open_parquet(path) / open_parquet(path, typed). Auto-routes columns tagged with " +
        "Heliosoph.DatumV.kind metadata back to their typed kind (Mesh / PointCloud / Image / " +
        "Audio / Video) when typed is true (the default); pass typed=false for raw byte arrays.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: null),
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("typed", DataKindMatcher.Exact(DataKind.Boolean)),
            ],
            FixedOutputSchema: null),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length is < 1 or > 2)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 or 2 arguments: open_parquet(path) or open_parquet(path, typed).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be STRING.");
        }
        if (constantArguments[0] is not DataValue pathValue || pathValue.Kind != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (path) must be a constant STRING. " +
                "Inline the file path or pass it via a bound $parameter.");
        }

        bool autoType = true;
        if (argumentKinds.Length == 2)
        {
            if (argumentKinds[1] != DataKind.Boolean)
            {
                throw new FunctionArgumentException(Name,
                    "argument 2 (typed) must be BOOLEAN.");
            }
            if (constantArguments[1] is not DataValue typedValue || typedValue.Kind != DataKind.Boolean)
            {
                throw new FunctionArgumentException(Name,
                    "argument 2 (typed) must be a constant BOOLEAN.");
            }
            autoType = typedValue.AsBoolean();
        }

        string path = pathValue.AsString(constantStore);
        if (!File.Exists(path))
        {
            throw new FunctionArgumentException(Name, $"Parquet file not found: '{path}'.");
        }

        OutputColumn[] columns = OpenAndBuildOutputColumns(path, autoType);
        return BuildSchemaFromOutputColumns(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length is < 1 or > 2)
        {
            throw new ArgumentException("open_parquet requires 1 or 2 arguments: (path) or (path, typed).");
        }

        string path = arguments[0].AsString();
        bool autoType = arguments.Length == 2 ? arguments[1].AsBoolean() : true;
        await foreach (RowBatch batch in StreamRowsAsync(path, autoType, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        bool autoType,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        await using Stream stream = File.OpenRead(path);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        OutputColumn[] columns = BuildOutputColumnsForReader(reader, autoType);
        string[] columnNames = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++) columnNames[i] = columns[i].Name;
        ColumnLookup outputLookup = new(columnNames);

        // Intern Struct TypeIds against the per-query TypeRegistry once for the
        // whole stream — every row of the same struct column shares the id,
        // and the registry dedups across columns with identical shapes.
        // Both top-level STRUCT and LIST<STRUCT> need the per-element struct
        // type-id stamped into every value emitted.
        ushort[] structTypeIds = new ushort[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            IReadOnlyList<ColumnInfo>? childInfos = columns[i] switch
            {
                StructOutputColumn s => s.BuildChildColumnInfos(),
                ListStructOutputColumn ls => ls.BuildChildColumnInfos(),
                _ => null,
            };
            if (childInfos is not null)
            {
                structTypeIds[i] = (ushort)context.Types.InternStructFromColumnInfoFields(childInfos);
            }
        }

        // Iterate row groups; for each, materialise every leaf column and
        // assemble per-row DataValues. v1 reads the whole row group into
        // memory at once; the chunked-streaming refactor is the planned
        // follow-up.
        RowBatch? batch = null;
        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(rg);
            int rowCount = checked((int)rgReader.RowCount);

            batch ??= context.RentRowBatch(outputLookup);

            // Per-output-column materialisation. Leaves produce one DataValue
            // array; struct columns produce one DataValue array per child
            // leaf and assemble them into FromStruct values per row.
            DataValue[][] perColumn = new DataValue[columns.Length][];
            for (int c = 0; c < columns.Length; c++)
            {
                perColumn[c] = columns[c] switch
                {
                    LeafOutputColumn leaf =>
                        await ReadLeafRowsAsync(leaf, rgReader, rowCount, batch.Arena, cancellationToken)
                            .ConfigureAwait(false),
                    StructOutputColumn structCol =>
                        await ReadStructRowsAsync(structCol, structTypeIds[c], rgReader, rowCount,
                            batch.Arena, cancellationToken).ConfigureAwait(false),
                    ListStructOutputColumn listStructCol =>
                        await ReadListStructRowsAsync(listStructCol, structTypeIds[c], rgReader,
                            rowCount, batch.Arena, cancellationToken).ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        $"Unexpected output column type {columns[c].GetType().Name}."),
                };
            }

            // Row assembly.
            for (int r = 0; r < rowCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataValue[] row = context.Pool.RentDataValues(columns.Length);
                for (int c = 0; c < columns.Length; c++)
                {
                    row[c] = perColumn[c][r];
                }
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                    batch ??= context.RentRowBatch(outputLookup);
                }
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Reads a flat (non-struct) Parquet column for the current row group and
    /// returns per-row <see cref="DataValue"/>s. Handles the typed-media
    /// route remap when the column carries a <c>datumv.kind</c> tag.
    /// </summary>
    private static async Task<DataValue[]> ReadLeafRowsAsync(
        LeafOutputColumn leaf,
        ParquetRowGroupReader rgReader,
        int rowCount,
        Arena arena,
        CancellationToken cancellationToken)
    {
        DataColumn col = await rgReader.ReadColumnAsync(leaf.Field, cancellationToken).ConfigureAwait(false);
        DataValue[] rawRows = ParquetColumnReader.ReadAsRows(col, leaf.Type, rowCount, arena);
        return leaf.Route is { } route
            ? ApplyTypedMediaRoute(rawRows, route, arena)
            : rawRows;
    }

    /// <summary>
    /// Reads every leaf belonging to a top-level <c>STRUCT</c> column for the
    /// current row group and assembles per-row <see cref="DataValue.FromStruct(DataValue[], IValueStore, ushort)"/>
    /// values. Children's typed-media routes are honoured per-leaf — the
    /// struct only adds the outer wrapping.
    /// </summary>
    private static async Task<DataValue[]> ReadStructRowsAsync(
        StructOutputColumn structCol,
        ushort structTypeId,
        ParquetRowGroupReader rgReader,
        int rowCount,
        Arena arena,
        CancellationToken cancellationToken)
    {
        int childCount = structCol.Children.Count;
        DataValue[][] childRows = new DataValue[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            childRows[i] = await ReadLeafRowsAsync(
                structCol.Children[i], rgReader, rowCount, arena, cancellationToken)
                .ConfigureAwait(false);
        }

        DataValue[] result = new DataValue[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            DataValue[] fields = new DataValue[childCount];
            for (int i = 0; i < childCount; i++) fields[i] = childRows[i][r];
            result[r] = DataValue.FromStruct(fields, arena, structTypeId);
        }
        return result;
    }

    /// <summary>
    /// Reads a top-level <c>LIST&lt;STRUCT&lt;…&gt;&gt;</c> column for the
    /// current row group and assembles per-outer-row Array&lt;Struct&gt;
    /// <see cref="DataValue"/>s. Each child leaf produces a flat
    /// <see cref="DataColumn"/> with shared repetition levels; the reader
    /// walks <c>rep == 0</c> markers to slice each leaf into per-outer-row
    /// element ranges and then builds per-element struct values plus the
    /// wrapping Array&lt;Struct&gt;.
    /// </summary>
    /// <remarks>
    /// v1 limitations mirror the export side: empty per-row lists fall
    /// through as zero-element Array&lt;Struct&gt; values (carrying no
    /// elements but a present array shape), per-element nulls are not
    /// surfaced (the writer rejects them up front), and struct children
    /// must be primitive leaves. The TypedMediaRoute pass that runs on
    /// scalar leaves is skipped here — no <c>datumv.kind</c> route applies
    /// to the leaves of a LIST&lt;STRUCT&gt; in v1.
    /// </remarks>
    private static async Task<DataValue[]> ReadListStructRowsAsync(
        ListStructOutputColumn col,
        ushort structTypeId,
        ParquetRowGroupReader rgReader,
        int rowCount,
        Arena arena,
        CancellationToken cancellationToken)
    {
        int childCount = col.Children.Count;
        DataColumn[] leafCols = new DataColumn[childCount];
        for (int i = 0; i < childCount; i++)
        {
            leafCols[i] = await rgReader.ReadColumnAsync(col.Children[i].Field, cancellationToken)
                .ConfigureAwait(false);
        }

        // Rep level streams are identical across every leaf of the same
        // list-of-struct (they describe the shared list grouping), so a
        // single walk against child 0 derives the per-row element ranges
        // for all leaves.
        int[]? repLevels = leafCols[0].RepetitionLevels;
        if (repLevels is null || repLevels.Length == 0)
        {
            // Degenerate: file declared LIST<STRUCT> but the column carries
            // no rep level stream. Emit empty arrays per outer row so the
            // schema still resolves cleanly.
            DataValue[] empty = new DataValue[rowCount];
            for (int r = 0; r < rowCount; r++)
            {
                empty[r] = DataValue.FromStructArray(System.Array.Empty<DataValue[]>(), arena, structTypeId);
            }
            return empty;
        }

        int[] rowStarts = new int[rowCount + 1];
        int currentRow = -1;
        for (int i = 0; i < repLevels.Length; i++)
        {
            if (repLevels[i] == 0)
            {
                currentRow++;
                if (currentRow >= rowCount)
                {
                    throw new InvalidDataException(
                        $"Parquet LIST<STRUCT> column '{col.Name}': repetition levels exceed row count {rowCount}.");
                }
                rowStarts[currentRow] = i;
            }
        }
        rowStarts[rowCount] = repLevels.Length;

        DataValue[] result = new DataValue[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = rowStarts[r];
            int end = rowStarts[r + 1];
            int elementCount = end - start;
            DataValue[][] elements = new DataValue[elementCount][];
            for (int e = 0; e < elementCount; e++)
            {
                DataValue[] fields = new DataValue[childCount];
                for (int c = 0; c < childCount; c++)
                {
                    fields[c] = ParquetColumnReader.BuildScalarAt(
                        leafCols[c].Data, start + e, col.Children[c].EffectiveKind, arena);
                }
                elements[e] = fields;
            }
            result[r] = DataValue.FromStructArray(elements, arena, structTypeId);
        }
        return result;
    }

    /// <summary>
    /// Per-column transformation plan derived from a tagged column's
    /// <c>datumv.kind</c> / <c>datumv.format</c> metadata. Carried alongside
    /// the column read so each row's bytes can be lifted into the typed
    /// <see cref="DataKind"/> with the right import path
    /// (.glb → <c>Mesh</c> via <see cref="GltfImporter"/>, PLY →
    /// <c>PointCloud</c> via <see cref="PlyImporter"/>, passthrough →
    /// kind retag with no decode).
    /// </summary>
    private readonly record struct TypedMediaRoute(DataKind TargetKind, string Format);

    private static TypedMediaRoute? TryParseRoute(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null) return null;
        if (!meta.TryGetValue(ParquetDatumvMetadata.KindKey, out string? kindName)) return null;
        if (!TryParseDataKind(kindName, out DataKind kind)) return null;
        // Format is optional in the metadata block (default passthrough). The
        // planner only emits routes that round-trip cleanly to a typed kind
        // — anything else stays as UInt8[].
        meta.TryGetValue(ParquetDatumvMetadata.FormatKey, out string? format);
        return new TypedMediaRoute(kind, format ?? ParquetDatumvMetadata.FormatPassthrough);
    }

    private static bool TryParseDataKind(string name, out DataKind kind)
    {
        // Only the typed kinds the writer ever emits are accepted here — a
        // malformed or future kind tag falls back to the column's raw type
        // rather than throwing, so a Heliosoph.DatumV build reading a file
        // produced by a later build degrades gracefully.
        switch (name)
        {
            case "Mesh": kind = DataKind.Mesh; return true;
            case "PointCloud": kind = DataKind.PointCloud; return true;
            case "Image": kind = DataKind.Image; return true;
            case "Audio": kind = DataKind.Audio; return true;
            case "Video": kind = DataKind.Video; return true;
            // Scalar kinds whose Parquet logical types lift back to a
            // different CLR type than the writer started with — the tag is
            // what recovers the source kind. Date (DateOnly → Parquet Date →
            // DateTime on read) and TimestampTz (DateTimeOffset → Timestamp
            // isAdjustedToUTC=true → DateTime on read) are the two that
            // actually need this; the rest of the scalar kinds round-trip
            // cleanly without metadata.
            case "Date": kind = DataKind.Date; return true;
            case "TimestampTz": kind = DataKind.TimestampTz; return true;
            // Json columns are stored on disk as plain UTF-8 strings (so
            // pandas / DuckDB / Spark can read them as text without a CBOR
            // decoder); the tag re-encodes them as canonical CBOR on read so
            // the engine's DataKind.Json contract (bytes are canonical CBOR)
            // survives the round trip.
            case "Json": kind = DataKind.Json; return true;
            default: kind = default; return false;
        }
    }

    private static DataValue[] ApplyTypedMediaRoute(DataValue[] rawRows, TypedMediaRoute route, Arena arena)
    {
        DataValue[] result = new DataValue[rawRows.Length];
        for (int i = 0; i < rawRows.Length; i++)
        {
            if (rawRows[i].IsNull)
            {
                result[i] = DataValue.Null(route.TargetKind);
                continue;
            }
            result[i] = route.TargetKind switch
            {
                // Byte-bag routes — the raw row is a UInt8[] (LIST<UInt8> on
                // disk) and the importer either parses or retags it.
                DataKind.Mesh when route.Format == ParquetDatumvMetadata.FormatGltf =>
                    DataValue.FromMesh(GltfImporter.Import(rawRows[i].AsUInt8Array(arena)), arena),
                DataKind.PointCloud when route.Format == ParquetDatumvMetadata.FormatPly =>
                    DataValue.FromPointCloud(PlyImporter.Import(rawRows[i].AsUInt8Array(arena)), arena),
                DataKind.Image => DataValue.FromImage(rawRows[i].AsUInt8Array(arena), arena),
                DataKind.Audio => DataValue.FromAudio(rawRows[i].AsUInt8Array(arena), arena),
                DataKind.Video => DataValue.FromVideo(rawRows[i].AsUInt8Array(arena), arena),

                // Scalar retag routes — the raw row is a DateTime-backed
                // Timestamp value (that's what Parquet.Net hands back for
                // both Parquet logical Date and Timestamp isAdjustedToUTC).
                // Narrow the carried DateTime back to the source kind so SQL
                // sees Date / TimestampTz rather than the lifted Timestamp.
                DataKind.Date =>
                    DataValue.FromDate(DateOnly.FromDateTime(rawRows[i].AsTimestamp())),
                DataKind.TimestampTz =>
                    DataValue.FromTimestampTz(new DateTimeOffset(rawRows[i].AsTimestamp(), TimeSpan.Zero)),

                // Json: raw row is a String DataValue carrying the canonical
                // JSON text from the writer. Re-encode it as CBOR (the
                // engine's on-the-wire shape for DataKind.Json) so downstream
                // code keeps working through the existing AsByteSpan path.
                DataKind.Json when route.Format == ParquetDatumvMetadata.FormatJsonText =>
                    DataValue.FromJson(
                        Heliosoph.DatumV.Functions.Json.CborJsonCodec.EncodeFromJsonText(
                            rawRows[i].AsString(arena)),
                        arena),

                // Unknown (kind, format) combination — leave the row as the
                // raw read-side value rather than throwing. Same robustness
                // story as the unknown-kind fallback in TryParseDataKind.
                _ => rawRows[i],
            };
        }
        return result;
    }

    /// <summary>
    /// Opens the file, walks <c>reader.Schema.Fields</c>, and returns the
    /// output column tree — one entry per top-level Parquet field. Top-level
    /// <see cref="StructField"/>s become <see cref="StructOutputColumn"/>s
    /// with the child leaves carried alongside; everything else becomes a
    /// flat <see cref="LeafOutputColumn"/>.
    /// </summary>
    private static OutputColumn[] OpenAndBuildOutputColumns(string path, bool autoType)
    {
        using Stream stream = File.OpenRead(path);
        using ParquetReader reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
        return BuildOutputColumnsForReader(reader, autoType);
    }

    /// <summary>
    /// Walks the open <see cref="ParquetReader"/>'s top-level fields and
    /// builds the per-output-column read plan, using the first row group's
    /// column metadata to detect <c>datumv.kind</c> tags on leaves when
    /// <paramref name="autoType"/> is true.
    /// </summary>
    private static OutputColumn[] BuildOutputColumnsForReader(ParquetReader reader, bool autoType)
    {
        // Per-leaf-DataField metadata sweep (first row group only — the
        // writer emits identical custom metadata on every flush).
        Dictionary<DataField, IReadOnlyDictionary<string, string>>? perLeafMeta = null;
        if (autoType && reader.RowGroupCount > 0)
        {
            perLeafMeta = new Dictionary<DataField, IReadOnlyDictionary<string, string>>();
            using ParquetRowGroupReader probe = reader.OpenRowGroupReader(0);
            foreach (DataField df in reader.Schema.GetDataFields())
            {
                Dictionary<string, string>? meta = probe.GetCustomMetadata(df);
                if (meta is not null && meta.Count > 0)
                {
                    perLeafMeta[df] = meta;
                }
            }
        }

        IReadOnlyList<Field> topFields = reader.Schema.Fields;
        OutputColumn[] result = new OutputColumn[topFields.Count];
        for (int i = 0; i < topFields.Count; i++)
        {
            result[i] = BuildOutputColumn(topFields[i], perLeafMeta);
        }
        return result;
    }

    /// <summary>
    /// Per top-level Parquet field, builds the output column. v1 supports
    /// flat <see cref="DataField"/>s and one-level <see cref="StructField"/>s
    /// whose children are themselves flat <see cref="DataField"/>s. Deeper
    /// nesting, <see cref="ListField"/>, and <see cref="MapField"/> throw with
    /// a column-named error so callers can adjust the source query without
    /// opening the file blind.
    /// </summary>
    private static OutputColumn BuildOutputColumn(
        Field field,
        IReadOnlyDictionary<DataField, IReadOnlyDictionary<string, string>>? perLeafMeta)
    {
        if (field is DataField df)
        {
            return BuildLeaf(field.Name, df, perLeafMeta);
        }
        // Top-level LIST<STRUCT<…>>: each element is a struct of primitives.
        // The on-disk shape is one DataColumn per struct child, all sharing
        // the same per-element repetition / definition level streams that
        // demarcate per-row list boundaries. The read path walks rep == 0
        // markers to slice each leaf into per-outer-row buckets and then
        // assembles per-element Struct DataValues into an Array<Struct>.
        if (field is ListField topList && topList.Item is StructField listStruct)
        {
            LeafOutputColumn[] children = new LeafOutputColumn[listStruct.Fields.Count];
            for (int i = 0; i < listStruct.Fields.Count; i++)
            {
                Field child = listStruct.Fields[i];
                if (child is not DataField childDf)
                {
                    throw new FunctionArgumentException(Name,
                        $"Parquet LIST<STRUCT> column '{field.Name}' contains a non-primitive child " +
                        $"'{child.Name}' ({child.SchemaType}). Nested complex children are not " +
                        "supported by open_parquet yet.");
                }
                children[i] = BuildLeaf(childDf.Name, childDf, perLeafMeta);
            }
            return new ListStructOutputColumn(field.Name, Nullable: true, Children: children);
        }
        if (field is StructField sf)
        {
            LeafOutputColumn[] children = new LeafOutputColumn[sf.Fields.Count];
            for (int i = 0; i < sf.Fields.Count; i++)
            {
                Field child = sf.Fields[i];
                // Plain primitive struct child.
                if (child is DataField childDf)
                {
                    children[i] = BuildLeaf(childDf.Name, childDf, perLeafMeta);
                    continue;
                }
                // LIST<primitive> struct child — unwrap the inner DataField
                // and surface it with the array shape. The leaf's own
                // IsArray flag is false (the wrapping ListField carries the
                // array signal), so we build the LeafOutputColumn by hand
                // and stamp EffectiveIsArray=true so the reader drives
                // through ParquetColumnReader's array branch instead of
                // the scalar branch.
                if (child is ListField lf && lf.Item is DataField listLeaf)
                {
                    children[i] = BuildListLeaf(child.Name, listLeaf, perLeafMeta);
                    continue;
                }
                throw new FunctionArgumentException(Name,
                    $"Parquet STRUCT column '{sf.Name}' contains a nested non-primitive child " +
                    $"'{child.Name}' ({child.SchemaType}). Nested STRUCT / LIST<STRUCT> / MAP children " +
                    "are not supported by open_parquet yet — flatten the schema or use " +
                    "open_parquet_meta to inspect the file.");
            }
            return new StructOutputColumn(sf.Name, Nullable: true, Children: children);
        }
        throw new FunctionArgumentException(Name,
            $"Parquet column '{field.Name}' is a {field.SchemaType} field, which open_parquet does " +
            "not yet support. (Supported: primitive scalar / 1-D array columns and one-level STRUCT " +
            "columns whose children are primitives.)");
    }

    /// <summary>
    /// Builds a flat-leaf <see cref="OutputColumn"/> for <paramref name="df"/>,
    /// resolving its column type, applying any <c>datumv.kind</c> route from
    /// <paramref name="perLeafMeta"/>, and rejecting unsupported types with a
    /// column-named error when no route rescues them.
    /// </summary>
    private static LeafOutputColumn BuildLeaf(
        string name,
        DataField df,
        IReadOnlyDictionary<DataField, IReadOnlyDictionary<string, string>>? perLeafMeta)
    {
        ParquetColumnType type = ParquetColumnType.From(df);

        TypedMediaRoute? route = null;
        if (perLeafMeta is not null
            && perLeafMeta.TryGetValue(df, out IReadOnlyDictionary<string, string>? meta)
            && TryParseRoute(meta) is { } parsedRoute)
        {
            route = parsedRoute;
        }

        DataKind effectiveKind = route?.TargetKind ?? type.ElementKind;
        bool effectiveIsArray = route is null && type.IsArray;

        if (route is null && !type.IsSupported)
        {
            throw new FunctionArgumentException(Name,
                $"Parquet column '{name}' has unsupported type " +
                $"(CLR={df.ClrType.Name}). " +
                "Use open_parquet_meta to inspect the file's column types.");
        }

        return new LeafOutputColumn(name, df, type, route, effectiveKind, effectiveIsArray);
    }

    /// <summary>
    /// Builds a leaf for a primitive <see cref="DataField"/> that is the
    /// inner element of a <see cref="ListField"/> — the leaf's own
    /// <see cref="DataField.IsArray"/> is false (the array signal lives on
    /// the wrapping <see cref="ListField"/>), so we synthesise a
    /// <see cref="ParquetColumnType"/> with <c>IsArray=true</c> and stamp
    /// <see cref="LeafOutputColumn.EffectiveIsArray"/> so downstream
    /// readers go through the per-row array-slice branch instead of
    /// trying to decode a 3-element column as 3 scalar rows.
    /// </summary>
    private static LeafOutputColumn BuildListLeaf(
        string listName,
        DataField listLeaf,
        IReadOnlyDictionary<DataField, IReadOnlyDictionary<string, string>>? perLeafMeta)
    {
        ParquetColumnType raw = ParquetColumnType.From(listLeaf);
        ParquetColumnType arrayType = raw with { IsArray = true };

        TypedMediaRoute? route = null;
        if (perLeafMeta is not null
            && perLeafMeta.TryGetValue(listLeaf, out IReadOnlyDictionary<string, string>? meta)
            && TryParseRoute(meta) is { } parsedRoute)
        {
            route = parsedRoute;
        }

        DataKind effectiveKind = route?.TargetKind ?? arrayType.ElementKind;
        bool effectiveIsArray = route is null;

        if (route is null && !arrayType.IsSupported)
        {
            throw new FunctionArgumentException(Name,
                $"Parquet LIST column '{listName}' has unsupported element type " +
                $"(CLR={listLeaf.ClrType.Name}). Use open_parquet_meta to inspect the file's column types.");
        }

        return new LeafOutputColumn(listName, listLeaf, arrayType, route, effectiveKind, effectiveIsArray);
    }

    /// <summary>
    /// Turns the output-column tree into a SQL <see cref="Schema"/>. Struct
    /// columns surface as <see cref="DataKind.Struct"/> with their child
    /// <see cref="ColumnInfo"/>s wired through so downstream operators can
    /// see field names without re-opening the file.
    /// </summary>
    private static Schema BuildSchemaFromOutputColumns(OutputColumn[] columns)
    {
        ColumnInfo[] infos = new ColumnInfo[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            infos[i] = columns[i].ToColumnInfo();
        }
        return new Schema(infos);
    }

    /// <summary>
    /// Discriminated base for top-level output columns the function emits.
    /// One of <see cref="LeafOutputColumn"/> or <see cref="StructOutputColumn"/>.
    /// </summary>
    private abstract record OutputColumn(string Name)
    {
        public abstract ColumnInfo ToColumnInfo();
    }

    /// <summary>
    /// Flat (non-struct) output column: a primitive scalar, a 1-D primitive
    /// array, or a typed-media kind routed back from a <c>datumv.kind</c>
    /// tag. Carries the leaf <see cref="DataField"/> directly so the read
    /// path can call <see cref="ParquetRowGroupReader.ReadColumnAsync"/>
    /// against it.
    /// </summary>
    private sealed record LeafOutputColumn(
        string Name,
        DataField Field,
        ParquetColumnType Type,
        TypedMediaRoute? Route,
        DataKind EffectiveKind,
        bool EffectiveIsArray) : OutputColumn(Name)
    {
        public override ColumnInfo ToColumnInfo() =>
            new(Name, EffectiveKind, nullable: Type.IsNullable) { IsArray = EffectiveIsArray };
    }

    /// <summary>
    /// Top-level <c>STRUCT</c> output column whose children are all flat
    /// leaves. The read path materialises every child's per-row
    /// <see cref="DataValue"/>s, then assembles per-row
    /// <see cref="DataValue.FromStruct(DataValue[], IValueStore, ushort)"/>
    /// values around them. v1 limits depth to one level — see
    /// <see cref="BuildOutputColumn"/> for the rejection path.
    /// </summary>
    private sealed record StructOutputColumn(
        string Name,
        bool Nullable,
        IReadOnlyList<LeafOutputColumn> Children) : OutputColumn(Name)
    {
        public IReadOnlyList<ColumnInfo> BuildChildColumnInfos()
        {
            ColumnInfo[] infos = new ColumnInfo[Children.Count];
            for (int i = 0; i < Children.Count; i++)
            {
                infos[i] = Children[i].ToColumnInfo();
            }
            return infos;
        }

        public override ColumnInfo ToColumnInfo() =>
            new(Name, nullable: Nullable, fields: BuildChildColumnInfos());
    }

    /// <summary>
    /// Top-level <c>LIST&lt;STRUCT&lt;…&gt;&gt;</c> output column. On read,
    /// each child leaf produces a flat <see cref="DataColumn"/> with shared
    /// repetition levels; the reader walks rep == 0 markers to slice the
    /// flat data into per-outer-row buckets and assembles per-element
    /// <see cref="DataValue.FromStruct(DataValue[], IValueStore, ushort)"/>
    /// values, then wraps the per-row element list in
    /// <see cref="DataValue.FromStructArray(System.ReadOnlySpan{DataValue[]}, IValueStore, ushort)"/>.
    /// SQL surface is <see cref="DataKind.Struct"/> with
    /// <see cref="ColumnInfo.IsArray"/>=true.
    /// </summary>
    private sealed record ListStructOutputColumn(
        string Name,
        bool Nullable,
        IReadOnlyList<LeafOutputColumn> Children) : OutputColumn(Name)
    {
        public IReadOnlyList<ColumnInfo> BuildChildColumnInfos()
        {
            ColumnInfo[] infos = new ColumnInfo[Children.Count];
            for (int i = 0; i < Children.Count; i++)
            {
                infos[i] = Children[i].ToColumnInfo();
            }
            return infos;
        }

        public override ColumnInfo ToColumnInfo() =>
            new(Name, nullable: Nullable, fields: BuildChildColumnInfos()) { IsArray = true };
    }
}
