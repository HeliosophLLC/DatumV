using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Catalog;

/// <summary>
/// A storage backend for tables in a <see cref="TableCatalog"/>.
/// A catalog mounts one or more backends; each owns a set of schemas
/// and is responsible for the physical representation of tables in
/// those schemas (file layout, on-disk indexes, manifest persistence).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TableCatalog"/> is the public facade — it owns SQL planning,
/// validation, the buffer pool, the UDF/Procedure registries, and routes
/// table lookup to the appropriate backend based on
/// <see cref="QualifiedName.Schema"/>. Backends never see SQL AST; they
/// only see post-validation requests like "create this table with this
/// schema at this path."
/// </para>
/// <para>
/// The S1b.1 implementation has one backend
/// (<see cref="FlatFileCatalog"/>) that owns everything. S1b.2 will split
/// system / virtual tables into their own read-only backends.
/// </para>
/// </remarks>
public interface ITableCatalog : IDisposable
{
    /// <summary>
    /// Schemas this backend owns. Used by the facade's router to
    /// dispatch lookups by <see cref="QualifiedName.Schema"/>.
    /// </summary>
    IReadOnlyCollection<string> Schemas { get; }

    /// <summary>
    /// True when this backend accepts CREATE TABLE / DROP TABLE /
    /// CREATE INDEX / DROP INDEX / ALTER TABLE requests. False for
    /// projection backends (the future SystemCatalog / VirtualCatalog).
    ///
    /// IMPORTANT: this describes the DDL surface, not data mutability.
    /// Per-table DML capability lives on <see cref="ITableProvider"/>
    /// (CanAppendRows, CanAlterColumns, …) and is unchanged.
    /// </summary>
    bool SupportsDdl { get; }

    /// <summary>Total number of tables registered with this backend.</summary>
    int Count { get; }

    /// <summary>
    /// Resolves <paramref name="name"/> to the registered provider, or
    /// returns false if no table with that qualified name exists in this
    /// backend.
    /// </summary>
    bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider);

    /// <summary>Enumerates every registered provider in this backend.</summary>
    IEnumerable<ITableProvider> ListTables();

    /// <summary>
    /// Registers an already-constructed provider. Used by hosts attaching
    /// projections (e.g. <c>ModelHost.AttachTo</c>), by
    /// tests adding <see cref="Providers.InMemoryTableProvider"/>, and by
    /// the facade's CREATE TEMP TABLE path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A provider with the same <see cref="ITableProvider.QualifiedName"/>
    /// is already registered.
    /// </exception>
    ITableProvider Add(ITableProvider provider);

    /// <summary>
    /// Removes the table from this backend's registry. Returns true if
    /// the table existed and was removed. The backend is responsible for
    /// disposing the provider and cleaning up any on-disk artifacts it
    /// owns (.datum, sidecars). Throws when
    /// <see cref="SupportsDdl"/> is false.
    /// </summary>
    bool DropTable(QualifiedName name);

    /// <summary>
    /// Creates a persistent <c>.datum</c>-backed table: resolves a path,
    /// materialises an empty file with the given schema (column descriptors,
    /// defaults, computed expressions, identity spec, PK), creates the PK
    /// sidecar if applicable, registers the resulting provider, and persists
    /// the catalog manifest. Throws when <see cref="SupportsDdl"/> is false.
    /// </summary>
    /// <param name="name">Canonical <c>(schema, table)</c> identity.</param>
    /// <param name="schema">Post-validation schema from the facade.</param>
    /// <param name="explicitStoragePath">
    /// The path from <c>CREATE TABLE … AT 'path'</c>, or <see langword="null"/>
    /// to let the backend derive the path from its own conventions.
    /// </param>
    /// <param name="primaryKeyConstraintName">
    /// User-supplied <c>CONSTRAINT name PRIMARY KEY</c>, or
    /// <see langword="null"/> when no custom name was given (the backend
    /// derives <c>&lt;table&gt;_pkey</c> at read time).
    /// </param>
    ITableProvider CreatePersistentTable(
        QualifiedName name,
        Model.Schema schema,
        string? explicitStoragePath,
        string? primaryKeyConstraintName);

    /// <summary>
    /// Records a new index in this backend's persistent state so it
    /// survives catalog reopen. The sidecar file creation is the
    /// provider's responsibility (it ran before this is called); this
    /// method only updates the manifest.
    /// </summary>
    void RegisterIndex(QualifiedName tableName, IndexDescriptor descriptor);

    /// <summary>
    /// Removes <paramref name="indexName"/> from this backend's persistent
    /// state. Returns true if the index existed (so the facade can persist
    /// and skip the IF EXISTS branch). The owning table is returned in
    /// <paramref name="ownerTable"/>.
    /// </summary>
    bool UnregisterIndex(string indexName, out QualifiedName ownerTable);

    /// <summary>
    /// Returns the user-defined indexes recorded for
    /// <paramref name="tableName"/>, or <see langword="null"/> when none.
    /// </summary>
    IReadOnlyList<IndexDescriptor>? GetTableIndexes(QualifiedName tableName);

    /// <summary>
    /// Resolves an index name to its owning table. Used by
    /// <c>DROP INDEX</c> (which doesn't name the table) and the
    /// <c>CREATE INDEX</c> catalog-global uniqueness check.
    /// </summary>
    bool TryGetIndexOwner(string indexName, out QualifiedName ownerTable);

    /// <summary>
    /// Returns the user-supplied PRIMARY KEY constraint name for
    /// <paramref name="tableName"/>, or <see langword="null"/> when no
    /// custom name was registered (the facade derives the PG default).
    /// </summary>
    string? GetCustomPrimaryKeyConstraintName(QualifiedName tableName);
}
