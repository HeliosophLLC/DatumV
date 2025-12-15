using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Base class for table providers that do not support index seeks. Supplies
/// the invariant boilerplate — name, pool, disposal, and the stubbed-out
/// optional-API surface — so concrete subclasses only need to implement
/// <see cref="GetRowCount"/>, <see cref="GetSchema"/>, and <see cref="ScanAsync"/>.
/// </summary>
public abstract class NonSeekableTableProviderBase : ITableProvider
{
    /// <summary>Default number of rows per yielded batch.</summary>
    protected const int DefaultBatchSize = 64;

    /// <summary>Buffer pool for renting row batches and value arrays.</summary>
    protected readonly Pool Pool;

    /// <summary>Initializes the provider with the given pool and logical table name.</summary>
    protected NonSeekableTableProviderBase(Pool pool, string name)
    {
        Pool = pool;
        Name = name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool Seekable => false;

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc/>
    public void Dispose() => Disposed = true;

    /// <inheritdoc/>
    public Manifest.QueryResultsManifest? GetManifest() => null;

    /// <inheritdoc/>
    public Indexing.SourceIndex? GetSourceIndex() => null;

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns, Arena? targetArena = null)
        => throw new NotSupportedException($"{GetType().Name} does not support seek sessions; use ScanAsync.");

    /// <inheritdoc/>
    public abstract long GetRowCount();

    /// <inheritdoc/>
    public abstract Schema GetSchema();

    /// <inheritdoc/>
    public abstract IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        CancellationToken cancellationToken);
}
