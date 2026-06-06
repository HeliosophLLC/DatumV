using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;
using HelioSchema = Heliosoph.DatumV.Model.Schema;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Single-file Apache Arrow IPC writer. One <see cref="ArrowFileWriter"/>
/// holds the open file; each input <see cref="RowBatch"/> becomes one
/// Arrow <see cref="RecordBatch"/>. The schema is built once at the first
/// non-empty batch (lazy-open so partial-file cleanup in <c>ExportPlan</c>
/// still works when the source query throws before yielding a row).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Typed-media columns</strong> carry their original
/// <see cref="DataKind"/> through the <c>datumv.kind</c> /
/// <c>datumv.format</c> / <c>datumv.version</c> field metadata
/// convention the Parquet sink uses. Image / Audio / Video bytes pass
/// through as Arrow <see cref="BinaryType"/>; Mesh and PointCloud are
/// converted to glTF and PLY respectively via <see cref="GltfExporter"/>
/// and <see cref="PlyExporter"/> so external tools see a standard
/// interchange format. Json decodes from CBOR to JSON text in a
/// <see cref="StringType"/> column. <c>open_arrow</c> doesn't yet read
/// these tags back, but the file is unambiguous; a future read-side
/// enhancement closes the loop the same way <c>open_parquet</c> does.
/// </para>
/// <para>
/// <strong>Record-batch granularity</strong> matches the engine's
/// <see cref="RowBatch"/> granularity — one Arrow RecordBatch per input
/// batch. No accumulation across batches.
/// </para>
/// </remarks>
internal sealed class ArrowExportSink : IExportSink
{
    private readonly string _path;
    private readonly HelioSchema _schema;
    private readonly SidecarRegistry? _sidecarRegistry;

    private FileStream? _stream;
    private ArrowFileWriter? _writer;
    private ArrowSchema? _arrowSchema;
    private bool _finished;
    private long _finalBytesWritten;
    private int[]? _sourceOrdinals;

    public ArrowExportSink(string path, HelioSchema schema, SidecarRegistry? sidecarRegistry)
    {
        _path = path;
        _schema = schema;
        _sidecarRegistry = sidecarRegistry;
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
                $"COPY TO arrow: sink for '{_path}' has already been finished; " +
                "cannot accept more rows.");
        }
        if (batch.Count == 0) return;

        await EnsureWriterOpenAsync(cancellationToken).ConfigureAwait(false);

        ColumnLookup lookup = batch.ColumnLookup;
        int[] sourceOrdinals = _sourceOrdinals ??= new int[_schema.Columns.Count];
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            if (!lookup.TryGetColumnOrdinal(_schema.Columns[i].Name, out int sourceOrd))
            {
                throw new ExportRuntimeException(
                    $"COPY TO arrow: batch is missing expected column '{_schema.Columns[i].Name}'. " +
                    "The source query's projection changed mid-stream.");
            }
            sourceOrdinals[i] = sourceOrd;
        }

        // Apache.Arrow builders are single-use — fresh instance per batch.
        IArrowColumnBuilder[] builders = new IArrowColumnBuilder[_schema.Columns.Count];
        for (int c = 0; c < builders.Length; c++)
        {
            builders[c] = ArrowColumnBuilderFactory.Create(_schema.Columns[c], _sidecarRegistry);
        }

        IValueStore store = batch.Arena;
        for (int r = 0; r < batch.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Row row = batch[r];
            for (int c = 0; c < builders.Length; c++)
            {
                builders[c].Append(row[sourceOrdinals[c]], store);
            }
            RowsWritten++;
        }

        IArrowArray[] columns = new IArrowArray[builders.Length];
        for (int c = 0; c < builders.Length; c++)
        {
            columns[c] = builders[c].Build();
        }
        using RecordBatch arrowBatch = new(_arrowSchema!, columns, length: batch.Count);
        await _writer!.WriteRecordBatchAsync(arrowBatch, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished) return;
        _finished = true;

        // Empty source still produces a valid 0-row Arrow file at the
        // target path so callers can tell "export ran" from "export
        // never started".
        await EnsureWriterOpenAsync(cancellationToken).ConfigureAwait(false);

        if (_writer is not null)
        {
            await _writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_stream is not null)
        {
            _finalBytesWritten = _stream.Length;
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

    private async Task EnsureWriterOpenAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null) return;

        ArrowField[] fields = new ArrowField[_schema.Columns.Count];
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            fields[i] = ArrowFieldBuilder.Build(_schema.Columns[i]);
        }
        _arrowSchema = new ArrowSchema(fields, metadata: null);

        _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
        _writer = new ArrowFileWriter(_stream, _arrowSchema, leaveOpen: true);
        await _writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
    }
}
