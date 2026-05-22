using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Executors;

/// <summary>
/// Owns the schema-level DDL pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>: <c>CREATE SCHEMA</c>,
/// <c>DROP SCHEMA</c>, and <c>SET search_path</c>.
/// </summary>
internal static class SchemaExecutor
{
    /// <summary>
    /// Applies <c>CREATE SCHEMA [IF NOT EXISTS] name</c>. The new schema
    /// is mounted on the user-data backend (FlatFile); user tables
    /// created with <c>CREATE TABLE name.t</c> land under it. Built-in
    /// schemas (public / system / information_schema / system)
    /// cannot be re-created.
    /// </summary>
    public static void CreateSchema(TableCatalog catalog, CreateSchemaStatement create, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(create);

        if (catalog.Backends.ContainsKey(create.SchemaName))
        {
            if (create.IfNotExists) return;

            throw new ExecutionException(
                $"Schema '{create.SchemaName}' already exists.");
        }

        catalog.Backends[create.SchemaName] = catalog.FlatFileCatalog;
        catalog.Events.Raise(new SchemaCreatedEvent(create.SchemaName, sourceText));
    }

    /// <summary>
    /// Applies <c>DROP SCHEMA [IF EXISTS] name [CASCADE | RESTRICT]</c>.
    /// RESTRICT (default) errors if the schema still contains tables;
    /// CASCADE drops every table in the schema first. Built-in schemas
    /// are protected.
    /// </summary>
    public static void DropSchema(TableCatalog catalog, DropSchemaStatement drop, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(drop);

        if (!catalog.Backends.TryGetValue(drop.SchemaName, out ITableCatalog? backend))
        {
            if (drop.IfExists) return;

            throw new ExecutionException(
                $"Schema '{drop.SchemaName}' does not exist.");
        }

        // Protect built-in schemas. The public schema is special — it's
        // the default home for user tables and tests rely on it being
        // present. system / information_schema / system are also
        // engine-managed.
        if (TableCatalog.IsBuiltinSchema(drop.SchemaName))
        {
            throw new InvalidOperationException(
                $"Schema '{drop.SchemaName}' is built-in and cannot be dropped.");
        }

        // Enumerate tables in this schema. Use the backend's listing
        // filtered by schema (case-insensitive).
        List<ITableProvider> tablesInSchema = backend.ListTables()
            .Where(p => string.Equals(p.QualifiedName.Schema, drop.SchemaName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tablesInSchema.Count > 0 && !drop.Cascade)
        {
            throw new InvalidOperationException(
                $"Cannot drop schema '{drop.SchemaName}' because it contains {tablesInSchema.Count} table(s). " +
                "Use DROP SCHEMA … CASCADE to drop the schema and its tables together.");
        }

        // CASCADE: drop every table in the schema first.
        foreach (ITableProvider provider in tablesInSchema)
        {
            backend.DropTable(provider.QualifiedName);
        }

        // Finally remove the routing entry so subsequent lookups fail.
        catalog.Backends.Remove(drop.SchemaName);

        // CASCADE-dropped child tables intentionally do NOT fire their own
        // TableDropped events — subscribers treat SchemaDropped as "blow
        // away the entire subtree" (see CatalogEvents class remarks).
        catalog.Events.Raise(new SchemaDroppedEvent(drop.SchemaName, sourceText));
    }

    /// <summary>
    /// Applies <c>SET search_path = a, b, c</c>. Replaces the session
    /// <see cref="TableCatalog.SearchPath"/> after validating that every
    /// named schema is mounted. In-flight queries that captured the prior
    /// path are unaffected — they keep their snapshot.
    /// </summary>
    public static void SetSearchPath(TableCatalog catalog, SetSearchPathStatement setSearchPath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(setSearchPath);

        catalog.SetSearchPath(setSearchPath.Schemas);
    }
}
