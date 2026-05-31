using System.IO.Compression;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;
using Parquet;
using Parquet.Schema;

namespace Heliosoph.DatumV.Export.Parquet;

/// <summary>
/// Single-file Parquet writer. One <see cref="ParquetColumnEncoder"/> per
/// output column buffers row values; when the buffered row count reaches the
/// configured row-group size the sink opens a
/// <see cref="ParquetRowGroupWriter"/>, flushes every encoder, and closes
/// the group. Closing the sink writes the Parquet footer.
/// </summary>
internal sealed class ParquetExportSink : IExportSink
{
    /// <summary>Default rows per row group when <c>ROW_GROUP_SIZE</c> is not specified.</summary>
    public const int DefaultRowGroupSize = 50_000;

    /// <summary>
    /// Bytes-buffered ceiling that triggers an early row-group flush. Set
    /// to 128 MiB — sized below Parquet.Net's 2 GB per-column writer cap
    /// with headroom for level streams and overheads, but high enough to
    /// produce reasonably large row groups for scalar workloads. Typed-
    /// media columns (Image / Mesh / etc.) routinely carry 50 KB–1 MB
    /// per row, so this is the trigger that actually fires for them.
    /// </summary>
    public const long DefaultRowGroupByteBudget = 128L * 1024L * 1024L;

    private readonly string _path;
    private readonly Schema _schema;
    private readonly int _rowGroupSize;
    private readonly long _rowGroupByteBudget;
    private readonly CompressionMethod _compressionMethod;
    private readonly CompressionLevel? _compressionLevel;
    private readonly ParquetColumnEncoder[] _encoders;
    private readonly ParquetSchema _parquetSchema;

    private FileStream? _stream;
    private ParquetWriter? _writer;
    private bool _finished;
    // Captured at FinishAsync time before the underlying FileStream is
    // closed and _stream is set to null — readers of BytesWritten that
    // come in after FinishAsync (notably the ExportPlan summary-row
    // builder) would otherwise see 0.
    private long _finalBytesWritten;

    public ParquetExportSink(
        string path,
        Schema schema,
        int rowGroupSize,
        SidecarRegistry? sidecarRegistry,
        long rowGroupByteBudget = DefaultRowGroupByteBudget,
        CompressionMethod compressionMethod = CompressionMethod.Snappy,
        CompressionLevel? compressionLevel = null)
    {
        _path = path;
        _schema = schema;
        _rowGroupSize = rowGroupSize;
        _rowGroupByteBudget = rowGroupByteBudget;
        _compressionMethod = compressionMethod;
        _compressionLevel = compressionLevel;

        _encoders = new ParquetColumnEncoder[schema.Columns.Count];
        Field[] fields = new Field[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            // The encoder factory closes over the registry for typed-media
            // kinds (Image / Audio / Video / Mesh / PointCloud) so sidecar-
            // backed values can resolve their storeId at append time without
            // threading the registry through every per-call signature.
            _encoders[i] = ParquetColumnEncoder.Create(schema.Columns[i], sidecarRegistry);
            fields[i] = _encoders[i].Field;
        }
        _parquetSchema = new ParquetSchema(fields);
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
                $"COPY TO parquet: sink for '{_path}' has already been finished; " +
                "cannot accept more rows.");
        }
        if (batch.Count == 0) return;

        await EnsureWriterAsync(cancellationToken).ConfigureAwait(false);

        // Map source-batch column ordinals to target-schema ordinals once per
        // batch. The source query's projection may emit columns in a different
        // order than the schema declares (and the lookup is case-insensitive).
        // The simplest robust approach: drive by target schema, look up each
        // column in the batch's ColumnLookup.
        ColumnLookup lookup = batch.ColumnLookup;
        int[] sourceOrdinals = new int[_schema.Columns.Count];
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (!lookup.TryGetColumnOrdinal(_schema.Columns[i].Name, out int sourceOrd))
            {
                throw new ExportRuntimeException(
                    $"COPY TO parquet: batch is missing expected column '{_schema.Columns[i].Name}'. " +
                    "The source query's projection changed mid-stream.");
            }
            sourceOrdinals[i] = sourceOrd;
        }

        IValueStore store = batch.Arena;
        for (int r = 0; r < batch.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row row = batch[r];
            for (int c = 0; c < _encoders.Length; c++)
            {
                _encoders[c].Append(row[sourceOrdinals[c]], store);
            }
            RowsWritten++;

            // Two flush triggers: row count (set by ROW_GROUP_SIZE; reasonable
            // for scalar-only exports) and aggregated buffered bytes (the
            // load-bearing trigger for typed-media exports where a few hundred
            // rows of mesh / image bytes can already push the per-column
            // buffer near Parquet.Net's 2 GB internal writer cap). The byte
            // sum is across all columns; a single fat column can starve the
            // row trigger but the byte trigger will still fire.
            if (_encoders[0].Count >= _rowGroupSize
                || BufferedBytesAcrossEncoders() >= _rowGroupByteBudget)
            {
                await FlushRowGroupAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private long BufferedBytesAcrossEncoders()
    {
        long total = 0L;
        for (int c = 0; c < _encoders.Length; c++)
        {
            total += _encoders[c].BufferedBytes;
        }
        return total;
    }

    /// <inheritdoc />
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished) return;
        _finished = true;

        // Even when no rows were written we still want a valid (empty) Parquet
        // file at the target path so callers can distinguish "the export ran"
        // from "the export never started".
        await EnsureWriterAsync(cancellationToken).ConfigureAwait(false);

        if (_encoders.Length > 0 && _encoders[0].Count > 0)
        {
            await FlushRowGroupAsync(cancellationToken).ConfigureAwait(false);
        }

        // Dispose the writer first so its trailing footer / index writes flush
        // into the underlying stream; only then can the stream length be read
        // as the canonical on-disk file size.
        if (_writer is not null)
        {
            _writer.Dispose();
            _writer = null;
        }
        if (_stream is not null)
        {
            _finalBytesWritten = _stream.Length;
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Defensive: if FinishAsync wasn't called, still close the underlying
        // resources so we don't leak file handles. The Parquet footer won't
        // be valid in that case — that's appropriate, since an aborted export
        // shouldn't leave a "good" file behind.
        if (_writer is not null)
        {
            try { _writer.Dispose(); } catch { /* swallow on abort */ }
            _writer = null;
        }
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    private async Task EnsureWriterAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null) return;
        _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        _writer = await ParquetWriter.CreateAsync(_parquetSchema, _stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        // Snappy is Parquet.Net's default — set it explicitly anyway so the
        // COPY-time option lands regardless of any future writer-default
        // change, and so the codec is visible in the resulting file's column-
        // chunk metadata for downstream tools to read.
        _writer.CompressionMethod = _compressionMethod;
        if (_compressionLevel is { } level)
        {
            _writer.CompressionLevel = level;
        }
    }

    private async Task FlushRowGroupAsync(CancellationToken cancellationToken)
    {
        // _writer is non-null because EnsureWriterAsync ran before any append.
        using ParquetRowGroupWriter rg = _writer!.CreateRowGroup();
        for (int c = 0; c < _encoders.Length; c++)
        {
            await _encoders[c].FlushAsync(rg, cancellationToken).ConfigureAwait(false);
        }
    }
}
