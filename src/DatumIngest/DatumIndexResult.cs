using DatumIngest.Indexing;

namespace DatumIngest;

/// <summary>
/// The output of a <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, CancellationToken)"/>
/// call: per-table index streams and a combined <see cref="SourceIndexSet"/>.
/// Dispose when uploads are complete.
/// </summary>
public sealed class DatumIndexResult : IAsyncDisposable
{
    /// <summary>
    /// Gets the source fingerprint computed from the <c>.datum</c> file.
    /// </summary>
    public required SourceFingerprint Fingerprint { get; init; }

    /// <summary>
    /// Gets the per-table index results, keyed by logical table name.
    /// </summary>
    public required IReadOnlyDictionary<string, DatumIndexTableResult> Tables { get; init; }

    /// <summary>
    /// Gets the combined source index set covering all indexed tables.
    /// </summary>
    public required SourceIndexSet IndexSet { get; init; }

    /// <summary>Total byte length of all <c>.datum-index</c> streams.</summary>
    public long IndexByteCount => Tables.Values.Sum(table => table.IndexByteCount);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        foreach (DatumIndexTableResult table in Tables.Values)
        {
            table.IndexStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
