using DatumIngest.DatumFile;
using DatumIngest.Model;
using DatumIngest.Output;

namespace DatumIngest.Output.Writers;

/// <summary>
/// Writes query results to the <c>.datum</c> binary column-store format.
/// </summary>
/// <remarks>
/// Row groups are written incrementally as rows arrive; a final row group is flushed on
/// <see cref="FinalizeAsync"/>. The file header is patched with the footer offset after
/// all data has been written, so the underlying stream must be seekable.
/// <para>
/// For large tensor or embedding workloads the writer auto-tunes the row group size
/// downward when any fixed-float column page would exceed 32 MiB uncompressed, capped
/// at a floor of 512 rows.
/// </para>
/// </remarks>
public sealed class DatumOutputWriter : IOutputWriter
{
    private readonly DatumFileWriter _fileWriter;
    private Schema? _schema;
    private long _rowsWritten;
    private readonly string? _filePath;

    /// <summary>
    /// Initializes a new instance of <see cref="DatumOutputWriter"/> that writes to the specified file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file to create.</param>
    public DatumOutputWriter(string filePath)
    {
        _filePath = filePath;
        _fileWriter = new DatumFileWriter(filePath);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DatumOutputWriter"/> that writes to a seekable stream.
    /// The caller retains ownership of the stream and is responsible for disposing it.
    /// </summary>
    /// <param name="stream">A writable, seekable stream to receive the datum bytes.</param>
    public DatumOutputWriter(Stream stream)
    {
        _fileWriter = new DatumFileWriter(stream);
    }

    /// <inheritdoc />
    public Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        DatumFileSchema datumSchema = DatumFileSchema.FromSchema(schema);
        _fileWriter.Initialize(datumSchema);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        // Wrap the single row in a RowBatch since DatumFileWriter only exposes a batch API.
        // The batch owns a lazy Arena which stays empty for rows with inline-only values;
        // rows with reference-type values rely on the caller having populated the row's
        // source arena, which DatumFileWriter cannot resolve here. This row-at-a-time path
        // is primarily used by the query-output pipeline where rows are inline-typed.
        RowBatch batch = RowBatch.Rent(1);
        batch.Add(row);
        _fileWriter.WriteRowBatch(batch);
        batch.Return();
        _rowsWritten++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        long bytesWritten = _fileWriter.Finalize();
        List<string> filesCreated = _filePath is not null ? [_filePath] : [];

        return Task.FromResult(new OutputSummary(_rowsWritten, bytesWritten, filesCreated));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _fileWriter.Dispose();
        return ValueTask.CompletedTask;
    }
}
