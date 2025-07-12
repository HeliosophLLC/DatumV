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
    private readonly Dictionary<string, TableDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ITableProvider>> _providerFactories = new(StringComparer.OrdinalIgnoreCase);

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
        TableDescriptor descriptor = Resolve(tableName);
        ITableProvider provider = CreateProvider(descriptor);
        return await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }
}
