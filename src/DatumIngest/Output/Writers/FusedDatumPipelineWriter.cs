using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Statistics;

namespace DatumIngest.Output.Writers;

/// <summary>
/// Writes query results to the <c>.datum</c> format while simultaneously accumulating a
/// source index and column statistics in a single streaming pass — avoiding a second scan.
/// </summary>
/// <remarks>
/// Pass an <see cref="IncrementalIndexBuilder"/> (created via
/// <see cref="SourceIndexBuilder.CreateIncrementalBuilder"/>) and/or a
/// <see cref="StatisticsCollector"/> at construction time. Both are optional; omitting them
/// makes this writer identical in behaviour to <see cref="DatumOutputWriter"/>.
/// <para>
/// When a file path is provided and an index builder is present, <see cref="FinalizeAsync"/>
/// serializes the completed index to a <c>.datum-index</c> sidecar file alongside the data file.
/// The completed index is also exposed via <see cref="CompletedIndex"/> for in-process consumers.
/// </para>
/// </remarks>
public sealed class FusedDatumPipelineWriter : IOutputWriter
{
    private readonly DatumFileWriter _fileWriter;
    private readonly IncrementalIndexBuilder? _indexBuilder;
    private readonly StatisticsCollector? _statisticsCollector;
    private readonly string? _filePath;
    private Schema? _schema;
    private long _rowsWritten;
    private SourceIndex? _completedIndex;

    /// <summary>
    /// Initializes a new <see cref="FusedDatumPipelineWriter"/> that writes to the specified file path
    /// and optionally co-generates a source index and column statistics.
    /// </summary>
    /// <param name="filePath">Absolute path to the <c>.datum</c> file to create.</param>
    /// <param name="indexBuilder">
    /// Optional incremental index builder for co-generation. Created via
    /// <see cref="SourceIndexBuilder.CreateIncrementalBuilder"/>.
    /// When provided, the completed index is written to <c>filePath + ".datum-index"</c> after finalization.
    /// </param>
    /// <param name="statisticsCollector">
    /// Optional statistics collector. When provided, each row is observed for column statistics.
    /// Retrieve results via <see cref="Statistics"/> after <see cref="FinalizeAsync"/>.
    /// </param>
    public FusedDatumPipelineWriter(
        string filePath,
        IncrementalIndexBuilder? indexBuilder = null,
        StatisticsCollector? statisticsCollector = null)
    {
        _filePath = filePath;
        _fileWriter = new DatumFileWriter(filePath);
        _indexBuilder = indexBuilder;
        _statisticsCollector = statisticsCollector;
    }

    /// <summary>
    /// Initializes a new <see cref="FusedDatumPipelineWriter"/> that writes to the given seekable stream.
    /// Index sidecar output is not available when using this constructor — the caller must handle
    /// serializing <see cref="CompletedIndex"/> if required.
    /// The caller retains ownership of the stream.
    /// </summary>
    /// <param name="stream">Writable, seekable stream to receive the datum bytes.</param>
    /// <param name="indexBuilder">Optional incremental index builder.</param>
    /// <param name="statisticsCollector">Optional statistics collector.</param>
    public FusedDatumPipelineWriter(
        Stream stream,
        IncrementalIndexBuilder? indexBuilder = null,
        StatisticsCollector? statisticsCollector = null)
    {
        _fileWriter = new DatumFileWriter(stream);
        _indexBuilder = indexBuilder;
        _statisticsCollector = statisticsCollector;
    }

    /// <summary>
    /// The schema passed to <see cref="InitializeAsync"/>, available after initialization.
    /// <c>null</c> before <see cref="InitializeAsync"/> has been called.
    /// </summary>
    public Schema? Schema => _schema;

    /// <summary>
    /// The completed source index, available after <see cref="FinalizeAsync"/> returns.
    /// <c>null</c> when no <see cref="IncrementalIndexBuilder"/> was provided at construction.
    /// </summary>
    /// <remarks>
    /// The returned index has <see cref="SourceIndex.SortedIndexes"/> set to <c>null</c>.
    /// Sorted index data remains on disk in the spill writer (accessible via <see cref="SortedIndexSpillWriter"/>)
    /// and should be streamed to the output via <see cref="IndexWriter"/>.
    /// </remarks>
    public SourceIndex? CompletedIndex => _completedIndex;

    /// <summary>
    /// The spill writer holding sorted index data on disk, available after <see cref="FinalizeAsync"/>.
    /// Pass to <see cref="UnifiedIndexWriter.Write(SourceIndexSet, Stream, SortedIndexSpillWriter)"/> for
    /// streaming serialization without materializing the full sorted index arrays.
    /// </summary>
    internal SortedIndexSpillWriter? SortedIndexSpillWriter => _indexBuilder?.SpillWriter;

    /// <summary>
    /// The column statistics, available after <see cref="FinalizeAsync"/> returns.
    /// <c>null</c> when no <see cref="StatisticsCollector"/> was provided at construction.
    /// </summary>
    public IReadOnlyDictionary<string, ColumnStatistics>? Statistics =>
        _statisticsCollector?.GetStatistics();

    /// <inheritdoc/>
    public Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        DatumFileSchema datumSchema = DatumFileSchema.FromSchema(schema);
        _fileWriter.Initialize(datumSchema);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a single row synchronously, bypassing the async state machine overhead
    /// of <see cref="WriteRowAsync"/>. Prefer this method in tight ingestion loops
    /// where every row is processed on the same thread.
    /// </summary>
    /// <param name="row">The row to write.</param>
    public void WriteRow(Row row)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        _fileWriter.WriteRow(row);
        _indexBuilder?.AddRow(row);
        _statisticsCollector?.AddRow(row, new Arena()); // TODO: remove with old ingestion
        _rowsWritten++;
    }

    /// <inheritdoc/>
    public Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        WriteRow(row);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        long bytesWritten = _fileWriter.Finalize();
        List<string> filesCreated = _filePath is not null ? [_filePath] : [];

        if (_indexBuilder is not null)
        {
            _completedIndex = _indexBuilder.Finalize();
            WriteIndexSidecar(_completedIndex, filesCreated);
        }

        return Task.FromResult(new OutputSummary(_rowsWritten, bytesWritten, filesCreated));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _fileWriter.Dispose();
        _indexBuilder?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ──────────────────── Private helpers ────────────────────

    private void WriteIndexSidecar(SourceIndex index, List<string> filesCreated)
    {
        if (_filePath is null)
        {
            return;
        }

        string tableName = FileFormatDetector.DeriveTableName(_filePath);
        SourceIndexSet indexSet = SourceIndexSet.Create(tableName, index);

        string indexPath = FileFormatDetector.GetSidecarBasePath(_filePath) + ".datum-index";
        using FileStream output = File.Create(indexPath);
        UnifiedIndexWriter.Write(indexSet, output, _indexBuilder?.SpillWriter);

        filesCreated.Add(indexPath);
    }
}
