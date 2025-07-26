using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Registry of named tables and their associated providers.
/// Resolves table names referenced in SQL FROM clauses to
/// <see cref="TableDescriptor"/> instances and creates the
/// appropriate <see cref="ITableProvider"/> for each.
/// </summary>
public sealed class TableCatalog
{
    /// <summary>
    /// Well-known option key stored on sub-table descriptors during multi-table expansion.
    /// The value is the sub-table qualifier used as the key in sidecar containers.
    /// </summary>
    public const string SubTableKeyOption = "datum:table_key";

    private readonly Dictionary<string, TableDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ITableProvider>> _providerFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QueryResultsManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a provider factory for a given provider identifier.
    /// </summary>
    /// <param name="providerName">Provider identifier (e.g. "csv", "json").</param>
    /// <param name="factory">Factory function that creates a new provider instance.</param>
    public void RegisterProvider(string providerName, Func<ITableProvider> factory)
    {
        _providerFactories[providerName] = factory;
    }

    /// <summary>
    /// Registers a table descriptor, making it available for resolution by name.
    /// </summary>
    /// <param name="descriptor">Table descriptor to register.</param>
    public void Register(TableDescriptor descriptor)
    {
        _descriptors[descriptor.Name] = descriptor;
    }

    /// <summary>
    /// Registers a table by name and file path, auto-detecting the provider
    /// from the file extension, filename pattern, or magic bytes.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined. Use the
    /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    /// </exception>
    public void Register(string name, string filePath)
    {
        Register(name, filePath, new Dictionary<string, string>());
    }

    /// <summary>
    /// Registers a table by name and file path with provider-specific options,
    /// auto-detecting the provider from the file extension, filename pattern,
    /// or magic bytes.
    /// </summary>
    /// <param name="name">Logical table name for SQL FROM clauses.</param>
    /// <param name="filePath">Absolute or relative path to the data file.</param>
    /// <param name="options">Provider-specific key-value options (e.g. delimiter, header).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the file format cannot be determined. Use the
    /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    /// </exception>
    public void Register(string name, string filePath, IReadOnlyDictionary<string, string> options)
    {
        string provider = FileFormatDetector.DetectProvider(filePath)
            ?? throw new ArgumentException(
                $"Cannot detect file format for '{filePath}'. " +
                "Supported formats: csv, json, jsonl, parquet, hdf5, zip, idx. " +
                "Use Register(TableDescriptor) with an explicit provider.",
                nameof(filePath));

        Register(new TableDescriptor(provider, name, filePath, options));
    }

    /// <summary>
    /// Resolves a table name to its descriptor.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <returns>The matching descriptor.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the table name is not registered.</exception>
    public TableDescriptor Resolve(string tableName)
    {
        if (_descriptors.TryGetValue(tableName, out TableDescriptor? descriptor))
        {
            return descriptor;
        }

        throw new KeyNotFoundException($"Table '{tableName}' is not registered in the catalog.");
    }

    /// <summary>
    /// Creates a provider instance for the given descriptor.
    /// </summary>
    /// <param name="descriptor">Table descriptor whose provider should be created.</param>
    /// <returns>A new provider instance.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no factory is registered for the provider.</exception>
    public ITableProvider CreateProvider(TableDescriptor descriptor)
    {
        if (_providerFactories.TryGetValue(descriptor.Provider, out Func<ITableProvider>? factory))
        {
            return factory();
        }

        throw new KeyNotFoundException($"No provider factory registered for '{descriptor.Provider}'.");
    }

    /// <summary>
    /// Returns all registered table names.
    /// </summary>
    public IEnumerable<string> TableNames => _descriptors.Keys;

    /// <summary>
    /// Returns all registered provider names.
    /// </summary>
    public IEnumerable<string> ProviderNames => _providerFactories.Keys;

    /// <summary>
    /// Attempts to resolve a table name without throwing.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <param name="descriptor">The matching descriptor, or null if not found.</param>
    /// <returns>True if the table was found; otherwise false.</returns>
    public bool TryResolve(string tableName, out TableDescriptor? descriptor)
    {
        return _descriptors.TryGetValue(tableName, out descriptor);
    }

    /// <summary>
    /// Resolves a table name and returns its schema by creating the appropriate
    /// provider and calling <see cref="ITableProvider.GetSchemaAsync"/>.
    /// </summary>
    /// <param name="tableName">Logical table name from the SQL query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schema of the named table.</returns>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when the table name or its provider is not registered.
    /// </exception>
    public async Task<Schema> GetSchemaAsync(string tableName, CancellationToken cancellationToken)
    {
        // Return cached schema from index if available, avoiding provider I/O.
        if (_indexes.TryGetValue(tableName, out SourceIndex? index))
        {
            return index.Schema.Schema;
        }

        TableDescriptor descriptor = Resolve(tableName);
        ITableProvider provider = CreateProvider(descriptor);
        return await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a pre-built source index for a table, enabling chunk-based
    /// partition pruning and cached schema resolution.
    /// </summary>
    /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    /// <param name="index">The source index to associate with the table.</param>
    public void RegisterIndex(string tableName, SourceIndex index)
    {
        _indexes[tableName] = index;
    }

    /// <summary>
    /// Attempts to retrieve a source index for the given table name.
    /// </summary>
    /// <param name="tableName">Logical table name.</param>
    /// <param name="index">The source index, or <c>null</c> if none is registered.</param>
    /// <returns><c>true</c> if an index was found; otherwise <c>false</c>.</returns>
    public bool TryGetIndex(string tableName, out SourceIndex? index)
    {
        return _indexes.TryGetValue(tableName, out index);
    }

    /// <summary>
    /// Registers a pre-computed <see cref="QueryResultsManifest"/> for a table,
    /// enabling statistics-driven cardinality estimation in the query planner.
    /// </summary>
    /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    /// <param name="manifest">The manifest containing per-column statistics.</param>
    public void RegisterManifest(string tableName, QueryResultsManifest manifest)
    {
        _manifests[tableName] = manifest;
    }

    /// <summary>
    /// Attempts to retrieve a <see cref="QueryResultsManifest"/> for the given table name.
    /// </summary>
    /// <param name="tableName">Logical table name.</param>
    /// <param name="manifest">The manifest, or <c>null</c> if none is registered.</param>
    /// <returns><c>true</c> if a manifest was found; otherwise <c>false</c>.</returns>
    public bool TryGetManifest(string tableName, out QueryResultsManifest? manifest)
    {
        return _manifests.TryGetValue(tableName, out manifest);
    }

    /// <summary>
    /// Expands multi-table sources by discovering sub-tables for providers that implement
    /// <see cref="IMultiTableSource"/>. Each discovered sub-table replaces the original
    /// registration with a qualified name (<c>{baseName}.{subTableName}</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExpandMultiTableSourcesAsync(CancellationToken cancellationToken)
    {
        // Snapshot keys to allow mutation during iteration.
        List<string> tableNames = new(_descriptors.Keys);

        foreach (string tableName in tableNames)
        {
            TableDescriptor descriptor = _descriptors[tableName];
            ITableProvider provider = CreateProvider(descriptor);

            if (provider is not IMultiTableSource multiTableSource)
            {
                continue;
            }

            IReadOnlyList<DiscoveredTable>? discovered = await multiTableSource
                .DiscoverTablesAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);

            if (discovered is null || discovered.Count == 0)
            {
                continue;
            }

            // Remove the original single-table registration.
            _descriptors.Remove(tableName);

            // Register each discovered sub-table with a qualified name.
            foreach (DiscoveredTable subTable in discovered)
            {
                string qualifiedName = $"{tableName}.{subTable.Name}";

                // Merge sub-table options with the table-key marker.
                Dictionary<string, string> mergedOptions = new(subTable.Options)
                {
                    [SubTableKeyOption] = subTable.Name
                };

                TableDescriptor subDescriptor = new(
                    descriptor.Provider,
                    qualifiedName,
                    descriptor.FilePath,
                    mergedOptions);

                _descriptors[qualifiedName] = subDescriptor;
            }
        }
    }
}
