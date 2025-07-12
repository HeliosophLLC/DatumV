namespace DatumIngest.Output;

using DatumIngest.Model;

/// <summary>
/// Interface for writing query results to an output format.
/// </summary>
public interface IOutputWriter : IAsyncDisposable
{
    /// <summary>
    /// Initializes the writer with the schema of the data to be written.
    /// Must be called before any write operations.
    /// </summary>
    Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a single row to the output.
    /// </summary>
    Task WriteRowAsync(Row row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes the output, flushing any buffered data.
    /// Returns a summary of what was written.
    /// </summary>
    Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary of an output write operation.
/// </summary>
/// <param name="RowsWritten">Total number of rows written.</param>
/// <param name="BytesWritten">Approximate total bytes written.</param>
/// <param name="FilesCreated">List of file paths created.</param>
public sealed record OutputSummary(long RowsWritten, long BytesWritten, IReadOnlyList<string> FilesCreated);
