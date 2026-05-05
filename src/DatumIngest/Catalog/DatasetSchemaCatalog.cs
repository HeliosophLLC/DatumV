using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Catalog;

/// <summary>
/// An <see cref="ITableCatalog"/> backend whose tables are driven by the
/// dataset manifest, not by user DDL. Mirrors the read-only spirit of
/// <see cref="ReadOnlyTableCatalog"/> but exposes an explicit
/// <see cref="SetTables"/> entry point so the dataset binder can refresh
/// the registered providers on install / uninstall events.
/// </summary>
/// <remarks>
/// <para>
/// One backend instance can own multiple schemas — every distinct
/// <c>DatasetEntry.Schema</c> the manifest declares. The hosting
/// <see cref="TableCatalog"/> registers the same instance under each
/// owned schema name; the per-schema entries in
/// <c>TableCatalog.Backends</c> all route to the same dict.
/// </para>
/// <para>
/// The full DDL surface (<see cref="CreatePersistentTable"/>,
/// <see cref="DropTable"/>, <see cref="RegisterIndex"/>, …) throws
/// <see cref="NotSupportedException"/> — datasets are catalog-managed
/// and have no user-DDL surface. <see cref="Add"/> also throws because
/// providers come exclusively from <see cref="SetTables"/>; tests / hosts
/// that need to attach ad-hoc providers should mount their own backend.
/// </para>
/// </remarks>
public sealed class DatasetSchemaCatalog : ITableCatalog
{
    private readonly HashSet<string> _schemas;
    // Atomic snapshot. SetTables swaps the reference wholesale so a
    // concurrent reader walking ListTables() never sees a torn dict.
    private volatile Dictionary<QualifiedName, ITableProvider> _tables = new();

    /// <summary>
    /// Creates a backend that owns the given schemas. Attempting to
    /// register a provider whose <see cref="QualifiedName.Schema"/> falls
    /// outside this set is an error.
    /// </summary>
    public DatasetSchemaCatalog(IEnumerable<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        _schemas = new HashSet<string>(schemas, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Schemas => _schemas;

    /// <inheritdoc/>
    public bool SupportsDdl => false;

    /// <inheritdoc/>
    public int Count => _tables.Count;

    /// <summary>
    /// Atomically replaces the registered providers with the given set.
    /// Providers that fell out (no longer present in the new snapshot)
    /// are disposed so their file handles release. Called by the dataset
    /// binder at boot and after each install / uninstall completes.
    /// </summary>
    public void SetTables(IEnumerable<ITableProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Dictionary<QualifiedName, ITableProvider> snapshot = new();
        foreach (ITableProvider p in providers)
        {
            if (!_schemas.Contains(p.QualifiedName.Schema))
            {
                throw new ArgumentException(
                    $"DatasetSchemaCatalog: provider '{p.QualifiedName}' references a schema " +
                    $"not owned by this backend ([{string.Join(", ", _schemas)}]).");
            }
            if (!snapshot.TryAdd(p.QualifiedName, p))
            {
                throw new ArgumentException(
                    $"DatasetSchemaCatalog: duplicate provider for '{p.QualifiedName}'. " +
                    "The binder must produce a unique (schema, name) per variant.");
            }
        }
        Dictionary<QualifiedName, ITableProvider> previous = _tables;
        _tables = snapshot;
        // Dispose every previous provider whose instance is NOT kept in
        // the new snapshot — both "key gone entirely" (uninstall) and
        // "key kept but a fresh provider is replacing it" (rebuild after
        // install / refresh). The instance check matters because each
        // RebuildAsync constructs a new DatumFileTableProviderV2 even
        // for unchanged variants; without this, the previous instance's
        // open .datum handle leaks until GC, and a subsequent uninstall
        // hits a sharing violation on Directory.Delete.
        foreach ((QualifiedName key, ITableProvider provider) in previous)
        {
            if (snapshot.TryGetValue(key, out ITableProvider? kept)
                && ReferenceEquals(kept, provider))
            {
                continue;
            }
            try { provider.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    /// <inheritdoc/>
    public bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider)
        => _tables.TryGetValue(name, out provider);

    /// <inheritdoc/>
    public IEnumerable<ITableProvider> ListTables() => _tables.Values;

    /// <inheritdoc/>
    public ITableProvider Add(ITableProvider provider)
        => throw new NotSupportedException(
            "DatasetSchemaCatalog: tables are driven by the dataset manifest. " +
            "Use SetTables() from the dataset binder; direct Add() is not supported.");

    /// <inheritdoc/>
    public bool DropTable(QualifiedName name)
    {
        if (_tables.ContainsKey(name))
        {
            throw new NotSupportedException(
                $"Schema '{name.Schema}' is dataset-managed — DROP TABLE is not supported. " +
                "Uninstall the dataset variant instead.");
        }
        return false;
    }

    /// <inheritdoc/>
    public ITableProvider CreatePersistentTable(
        QualifiedName name, Model.Schema schema, string? primaryKeyConstraintName)
        => throw new NotSupportedException(
            $"Schema '{name.Schema}' is dataset-managed — CREATE TABLE is not supported.");

    /// <inheritdoc/>
    public void RegisterIndex(QualifiedName tableName, IndexDescriptor descriptor)
        => throw new NotSupportedException(
            $"Schema '{tableName.Schema}' is dataset-managed — CREATE INDEX is not supported.");

    /// <inheritdoc/>
    public bool UnregisterIndex(string indexName, out QualifiedName ownerTable)
    {
        ownerTable = default;
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IndexDescriptor>? GetTableIndexes(QualifiedName tableName) => null;

    /// <inheritdoc/>
    public bool TryGetIndexOwner(string indexName, out QualifiedName ownerTable)
    {
        ownerTable = default;
        return false;
    }

    /// <inheritdoc/>
    public string? GetCustomPrimaryKeyConstraintName(QualifiedName tableName) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (ITableProvider provider in _tables.Values)
        {
            try { provider.Dispose(); }
            catch { /* best-effort */ }
        }
        _tables = new Dictionary<QualifiedName, ITableProvider>();
    }
}
