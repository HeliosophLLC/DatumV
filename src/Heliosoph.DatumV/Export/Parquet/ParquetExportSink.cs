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

    private readonly string _path;
    private readonly Schema _schema;
    private readonly int _rowGroupSize;
    private readonly ParquetColumnEncoder[] _encoders;
    private readonly ParquetSchema _parquetSchema;

    private FileStream? _stream;
    private ParquetWriter? _writer;
    private bool _finished;

    public ParquetExportSink(string path, Schema schema, int rowGroupSize)
    {
        _path = path;
        _schema = schema;
        _rowGroupSize = rowGroupSize;

        _encoders = new ParquetColumnEncoder[schema.Columns.Count];
        Field[] fields = new Field[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            _encoders[i] = ParquetColumnEncoder.Create(schema.Columns[i]);
            fields[i] = _encoders[i].Field;
        }
        _parquetSchema = new ParquetSchema(fields);
    }

    /// <inheritdoc />
    public long RowsWritten { get; private set; }

    /// <inheritdoc />
    public long BytesWritten => _stream?.Length ?? 0L;

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

            if (_encoders[0].Count >= _rowGroupSize)
            {
                await FlushRowGroupAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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

        if (_writer is not null)
        {
            _writer.Dispose();
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
