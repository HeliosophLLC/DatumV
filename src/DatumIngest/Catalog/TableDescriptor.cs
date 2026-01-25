namespace DatumIngest.Catalog;

/// <summary>
/// Controls which operations are permitted on a table.
/// </summary>
public enum TableMutability
{
    /// <summary>
    /// The table is read-only. No DDL or DML write operations are allowed.
    /// This is the default for externally sourced tables (CSV, Parquet, etc.).
    /// </summary>
    ReadOnly,

    /// <summary>
    /// The table is owned by the session that created it and supports full DDL/DML.
    /// This is the default for temporary tables created via <c>CREATE TEMP TABLE</c>.
    /// </summary>
    SessionOwned,

    /// <summary>
    /// The table is a persistent <c>.datum</c> table that supports full DDL/DML.
    /// Reserved for future use with <c>CREATE TABLE</c> (non-temporary).
    /// </summary>
    Writable,
}

/// <summary>
/// Describes a named table that can be opened by a provider.
/// </summary>
/// <param name="Name">Logical table name used in SQL FROM clauses.</param>
/// <param name="FilePath">Absolute or relative file path to the data source.</param>
/// <param name="Mutability">
/// Controls which operations are permitted on this table.
/// Defaults to <see cref="TableMutability.ReadOnly"/> for externally sourced tables.
/// </param>
/// <param name="PrimaryKeyColumns">
/// Column names that form the primary key, in declaration order.
/// When non-null and non-empty, INSERT operations enforce uniqueness.
/// </param>
/// <param name="Indexes">
/// User-defined secondary indexes built via <c>CREATE INDEX</c>. Maintained
/// in <c>.datum-cindex-{name}</c> sidecar files. Each entry names the index
/// and the ordered list of columns it covers. <see langword="null"/> means
/// no user-defined indexes (the auto-generated per-column acceleration trees
/// in <c>.datum-bptree-*</c> are a separate concern).
/// </param>
public sealed record TableDescriptor(
    string Name,
    string FilePath,
    TableMutability Mutability = TableMutability.ReadOnly,
    IReadOnlyList<string>? PrimaryKeyColumns = null,
    IReadOnlyList<IndexDescriptor>? Indexes = null);

/// <summary>
/// A user-defined secondary index built via <c>CREATE INDEX</c>. Persisted in
/// the catalog and materialised on disk as a <c>.datum-cindex-{Name}</c>
/// sidecar (a bytes-keyed mutable B+Tree backed by
/// <see cref="DatumIngest.Indexing.CompositeKeyEncoder"/>).
/// </summary>
/// <param name="Name">Index name (unique within the table; used as the sidecar filename).</param>
/// <param name="Columns">Ordered list of column names covered by the index.</param>
public sealed record IndexDescriptor(
    string Name,
    IReadOnlyList<string> Columns);
