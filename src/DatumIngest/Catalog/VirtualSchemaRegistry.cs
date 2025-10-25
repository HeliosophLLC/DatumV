#if false
namespace DatumIngest.Catalog;

/// <summary>
/// Registry of virtual schemas (e.g. <c>information_schema</c>, <c>datum_catalog</c>)
/// that can be addressed using schema-qualified table references in SQL.
/// Schema names are matched case-insensitively.
/// </summary>
public sealed class VirtualSchemaRegistry
{
    private readonly Dictionary<string, IVirtualSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a virtual schema. Throws if a schema with the same name is already registered.
    /// </summary>
    /// <param name="schema">The virtual schema to register.</param>
    /// <exception cref="ArgumentException">A schema with the same name is already registered.</exception>
    public void Register(IVirtualSchema schema)
    {
        if (!_schemas.TryAdd(schema.Name, schema))
        {
            throw new ArgumentException($"Virtual schema '{schema.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Attempts to resolve a virtual schema by name.
    /// </summary>
    /// <param name="schemaName">The schema name (e.g. <c>information_schema</c>).</param>
    /// <returns>The virtual schema, or <see langword="null"/> if no such schema exists.</returns>
    public IVirtualSchema? TryResolve(string schemaName)
    {
        _schemas.TryGetValue(schemaName, out IVirtualSchema? schema);
        return schema;
    }

    /// <summary>
    /// Returns the names of all registered virtual schemas.
    /// </summary>
    public IEnumerable<string> SchemaNames => _schemas.Keys;

    /// <summary>
    /// Creates a registry pre-populated with the built-in virtual schemas
    /// (<c>information_schema</c> and <c>datum_catalog</c>).
    /// </summary>
    public static VirtualSchemaRegistry CreateDefault()
    {
        VirtualSchemaRegistry registry = new();
        registry.Register(new VirtualSchemas.InformationSchemaDefinition());
        registry.Register(new VirtualSchemas.DatumCatalogDefinition());
        return registry;
    }
}
#endif