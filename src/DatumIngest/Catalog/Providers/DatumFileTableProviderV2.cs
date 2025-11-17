using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads v2 <c>.datum</c> files via the <see cref="DatumFileReaderV2"/>.
/// Yields one <see cref="RowBatch"/> per page (1024 rows by default),
/// projecting only the columns the caller asks for. Sidecar-backed
/// payloads (Image, large strings, byte arrays, Struct, …) decode through
/// the catalog's <see cref="SidecarRegistry"/> using
/// <see cref="SidecarStoreId"/>.
/// </summary>
/// <remarks>
/// First-cut implementation: no zone-map pruning, no seek session,
/// no <c>.datum-manifest</c> / <c>.datum-index</c> sidecar discovery
/// (those are v1 sidecars; v2-equivalents are Phase 5+ work). The
/// scan walks pages in order, opens fresh decoders per page, and yields
/// page-sized batches whose arena absorbs eagerly-materialized children
/// (Struct field arrays).
/// </remarks>
public sealed class DatumFileTableProviderV2 : ITableProvider, IDatumFileTableProvider, IDisposable
{
    private readonly TableDescriptor _descriptor;
    private readonly DatumFileReaderV2 _reader;
    private readonly Pool _pool;
    private readonly SidecarReadStore? _sidecar;
    private readonly Schema _schema;

    /// <summary>
    /// Initializes the provider with the given descriptor and pool. Opens
    /// the v2 <c>.datum</c> file, parses its footer, and (when the file
    /// declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>)
    /// memory-maps the companion <c>.datum-blob</c> for sidecar reads.
    /// Use <see cref="DatumFileTableProvider.Open"/> from
    /// <see cref="TableCatalog"/> rather than this constructor directly so
    /// v1 / v2 dispatch is centralized.
    /// </summary>
    public DatumFileTableProviderV2(TableDescriptor descriptor, Pool pool)
    {
        _descriptor = descriptor;
        _pool = pool;
        _reader = DatumFileReaderV2.Open(descriptor.FilePath);
        _sidecar = TryOpenSidecar(descriptor.FilePath, _reader);
        _schema = BuildSchema(_reader.Footer);
    }

    /// <inheritdoc/>
    public IBlobSource? Sidecar => _sidecar;

    /// <inheritdoc/>
    public byte SidecarStoreId { get; set; }

    /// <inheritdoc/>
    public string Name => _descriptor.Name;

    /// <inheritdoc/>
    public bool Seekable => false;

    /// <inheritdoc/>
    public long GetRowCount() => _reader.TotalRowCount;

    /// <inheritdoc/>
    public Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public QueryResultsManifest? GetManifest() => null;

    /// <inheritdoc/>
    public SourceIndex? GetSourceIndex() => null;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = filterHint; // Zone-map pruning deferred — see class remarks.

        if (_reader.Footer.Columns.Count == 0)
        {
            yield break;
        }

        // Resolve the column projection. Maps lookup-index → schema-index
        // so the scan only opens decoders for projected columns.
        ColumnLookup columnLookup = ResolveProjection(_schema, requiredColumns);
        int projectedCount = columnLookup.Count;
        if (projectedCount == 0)
        {
            yield break;
        }

        // All columns share the same page count and per-page row count
        // because the writer flushes every encoder at the same row cadence.
        // We probe column 0 and trust the rest match. (If a future writer
        // ever desyncs, this assumption needs to be lifted — but the
        // current spec has all encoders flushing on the same row.)
        int pageCount = _reader.Footer.Columns[0].Pages.Count;
        int[] schemaIndices = new int[projectedCount];
        for (int i = 0; i < projectedCount; i++)
        {
            schemaIndices[i] = columnLookup.GetSchemaColumnIndex(i);
        }

        IPageDecoderV2[] decoders = new IPageDecoderV2[projectedCount];

        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Page row count is the same across all columns (writer
            // invariant) — read it from the first projected column's page
            // descriptor.
            int rowCount = _reader.Footer.Columns[schemaIndices[0]].Pages[pageIndex].RowCount;
            if (rowCount == 0)
            {
                continue;
            }

            // One batch per page. Batch.Arena is the eager store the
            // decoders use to materialize Struct field arrays — values
            // stored against the batch's own arena resolve cleanly through
            // standard accessors downstream.
            RowBatch batch = _pool.RentRowBatch(columnLookup, rowCount);

            // Open page decoders bound to the batch's arena.
            for (int i = 0; i < projectedCount; i++)
            {
                decoders[i] = _reader.OpenPageDecoder(
                    columnIndex: schemaIndices[i],
                    pageIndex: pageIndex,
                    sidecarStoreId: SidecarStoreId,
                    sidecarSource: _sidecar,
                    eagerStore: batch.Arena);
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = _pool.RentDataValues(projectedCount);
                for (int i = 0; i < projectedCount; i++)
                {
                    values[i] = decoders[i].ReadValue(rowIndex);
                }
                batch.Add(values);
            }

            yield return batch;
        }
    }

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns)
    {
        throw new NotSupportedException(
            "v2 .datum reader does not yet support seek sessions. " +
            "Index-based seeks will land alongside the Phase 5 chapter-grained index work.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _sidecar?.Dispose();
        _reader.Dispose();
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Builds an engine-facing <see cref="Schema"/> from the v2 footer's
    /// column descriptors. <see cref="ColumnInfo.ArrayElementKind"/> is
    /// populated for typed-array columns so downstream type inference
    /// stays accurate.
    /// </summary>
    private static Schema BuildSchema(FooterV2 footer)
    {
        ColumnInfo[] columns = new ColumnInfo[footer.Columns.Count];
        for (int i = 0; i < footer.Columns.Count; i++)
        {
            ColumnDescriptorV2 d = footer.Columns[i].Descriptor;
            DataKind? arrayElementKind = d.IsArray ? d.Kind : null;
            columns[i] = new ColumnInfo(d.Name, d.Kind, d.IsNullable, arrayElementKind);
        }
        return new Schema(columns);
    }

    private static ColumnLookup ResolveProjection(Schema schema, IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            return new ColumnLookup(schema.Columns);
        }

        (int index, int schemaIndex, string name)[] projected = new (int, int, string)[requiredColumns.Count];
        int index = 0;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (requiredColumns.Contains(schema.Columns[i].Name))
            {
                projected[index] = (index, i, schema.Columns[i].Name);
                index++;
            }
        }
        return new ColumnLookup(projected);
    }

    /// <summary>
    /// Opens the companion <c>.datum-blob</c> sidecar when the v2 file
    /// declares <see cref="DatumFileFlagsV2.HasSidecarReferences"/>. The
    /// fingerprint is read from the sidecar's own header (v2 doesn't
    /// store the fingerprint in the .datum footer yet — see follow-up
    /// in <c>project_sidecar_integrity_hash.md</c>); the
    /// <see cref="SidecarReadStore"/>'s fingerprint check therefore only
    /// validates that the sidecar header itself is consistent.
    /// </summary>
    private static SidecarReadStore? TryOpenSidecar(string datumPath, DatumFileReaderV2 reader)
    {
        if ((reader.Header.Flags & DatumFileFlagsV2.HasSidecarReferences) == 0)
        {
            return null;
        }

        string sidecarPath = Path.ChangeExtension(datumPath, SidecarConstants.FileExtension);
        if (!File.Exists(sidecarPath))
        {
            throw new FileNotFoundException(
                $".datum file '{datumPath}' declares HasSidecarReferences but the companion sidecar " +
                $"'{sidecarPath}' is missing.", sidecarPath);
        }

        ulong fingerprint = ReadSidecarFingerprint(sidecarPath);
        return new SidecarReadStore(sidecarPath, fingerprint);
    }

    /// <summary>
    /// Reads the 8-byte fingerprint at offset 16 of the sidecar header
    /// (after the 8-byte magic + 4-byte version + 4-byte reserved fields,
    /// per <see cref="SidecarConstants"/>'s documented layout).
    /// </summary>
    private static ulong ReadSidecarFingerprint(string sidecarPath)
    {
        using FileStream fs = File.OpenRead(sidecarPath);
        Span<byte> hdr = stackalloc byte[SidecarConstants.HeaderSize];
        fs.ReadExactly(hdr);
        return BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(16, 8));
    }
}
