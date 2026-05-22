using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// An <see cref="ITableCatalog"/> backend for schemas whose tables are
/// engine projections — not user data. Hosts the <c>system.*</c>,
/// <c>information_schema.*</c>, and <c>system.*</c> providers
/// that surface UDFs, procedures, model metadata, schema introspection,
/// and catalog statistics as queryable virtual tables.
/// </summary>
/// <remarks>
/// <para>
/// The interface's full DDL surface (CREATE TABLE, DROP TABLE,
/// CREATE INDEX, DROP INDEX) throws <see cref="NotSupportedException"/>
/// here — user DDL never applies to these schemas. Providers are
/// attached at construction (system / virtual schemas) or by the host
/// at startup (e.g. <c>ModelHost.AttachTo</c> attaches
/// <see cref="Providers.ModelsTableProvider"/> after the catalog is
/// built).
/// </para>
/// <para>
/// Distinct instances host different schema sets:
/// </para>
/// <list type="bullet">
///   <item><description>The system instance owns <c>system</c>.</description></item>
///   <item><description>The virtual instance owns <c>information_schema</c> and <c>system</c>.</description></item>
/// </list>
/// <para>
/// The split exists because the two instances are conceptually
/// distinct, even though their implementation is identical. SQL-standard
/// metadata views (<c>information_schema.*</c>) and engine-specific
/// projections (<c>system.*</c>) live together because they're
/// equally read-only and projection-only; the <c>system</c> schema is
/// host-attached state (UDFs, procedures, models).
/// </para>
/// </remarks>
public sealed class ReadOnlyTableCatalog : ITableCatalog
{
    private readonly IReadOnlyCollection<string> _schemas;
    private readonly ConcurrentDictionary<QualifiedName, ITableProvider> _tables = new();

    /// <summary>
    /// Creates a backend that owns the given schemas. Adding a provider
    /// whose <see cref="QualifiedName.Schema"/> is not in this set is an
    /// error.
    /// </summary>
    public ReadOnlyTableCatalog(IReadOnlyCollection<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        // Normalise into a case-insensitive set for membership checks.
        _schemas = new HashSet<string>(schemas, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Schemas => _schemas;

    /// <inheritdoc/>
    public bool SupportsDdl => false;

    /// <inheritdoc/>
    public int Count => _tables.Count;

    /// <inheritdoc/>
    public bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider)
        => _tables.TryGetValue(name, out provider);

    /// <inheritdoc/>
    public IEnumerable<ITableProvider> ListTables() => _tables.Values;

    /// <inheritdoc/>
    public ITableProvider Add(ITableProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_schemas.Contains(provider.QualifiedName.Schema))
        {
            throw new ArgumentException(
                $"Cannot attach '{provider.QualifiedName}' to this backend: it owns " +
                $"[{string.Join(", ", _schemas)}], not schema '{provider.QualifiedName.Schema}'.");
        }
        if (!_tables.TryAdd(provider.QualifiedName, provider))
        {
            throw new ArgumentException(
                $"A table with the name '{provider.QualifiedName}' is already registered.");
        }
        return provider;
    }

    /// <inheritdoc/>
    public bool DropTable(QualifiedName name)
    {
        if (_tables.ContainsKey(name))
        {
            throw new NotSupportedException(
                $"Schema '{name.Schema}' is read-only — DROP TABLE is not supported.");
        }
        // Table doesn't exist here; return false so IF EXISTS can suppress.
        return false;
    }

    /// <inheritdoc/>
    public ITableProvider CreatePersistentTable(
        QualifiedName name, Model.Schema schema, string? primaryKeyConstraintName)
        => throw new NotSupportedException(
            $"Schema '{name.Schema}' is read-only — CREATE TABLE is not supported.");

    /// <inheritdoc/>
    public void RegisterIndex(QualifiedName tableName, IndexDescriptor descriptor)
        => throw new NotSupportedException(
            $"Schema '{tableName.Schema}' is read-only — CREATE INDEX is not supported.");

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
        _tables.Clear();
    }
}
