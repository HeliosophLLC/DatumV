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
/// The kind of secondary index a <see cref="IndexDescriptor"/> describes.
/// Maps 1:1 to a <c>USING &lt;method&gt;</c> in <c>CREATE INDEX</c>.
/// </summary>
public enum IndexKind
{
    /// <summary>
    /// Default. Composite B+Tree over one-or-more columns, materialised as
    /// a <c>.datum-cindex-{Name}</c> sidecar. The <c>USING</c> clause is
    /// absent in DDL.
    /// </summary>
    Composite = 0,

    /// <summary>
    /// Full-text inverted index over a single string column, materialised
    /// as a <c>.datum-fts-{column}</c> sidecar. DDL is
    /// <c>CREATE INDEX ... USING FTS(col) [WITH (analyzer = '...')]</c>.
    /// </summary>
    FullText = 1,
}

/// <summary>
/// A user-defined secondary index built via <c>CREATE INDEX</c>. Persisted in
/// the catalog. The on-disk sidecar shape depends on <see cref="Kind"/>:
/// <see cref="IndexKind.Composite"/> uses <c>.datum-cindex-{Name}</c>,
/// <see cref="IndexKind.FullText"/> uses <c>.datum-fts-{column}</c>.
/// </summary>
/// <param name="Name">Index name (unique within the table; used as the sidecar filename for composite indexes).</param>
/// <param name="Columns">
/// Ordered list of column names covered by the index. Full-text indexes
/// must have exactly one column.
/// </param>
/// <param name="IsUnique">
/// Only meaningful for <see cref="IndexKind.Composite"/>. When
/// <see langword="true"/> (<c>CREATE UNIQUE INDEX</c>), the backing tree is
/// opened with <c>allowDuplicates: false</c>; INSERTs that would produce a
/// second entry with the same encoded key throw a uniqueness violation.
/// Rows where any covered column is <c>NULL</c> are exempt from the check
/// (NULLS DISTINCT, PG default). Must be <see langword="false"/> for
/// full-text indexes — duplicates are the whole point.
/// </param>
/// <param name="Kind">
/// What flavour of index this is. Defaults to
/// <see cref="IndexKind.Composite"/> so pre-Kind catalog entries deserialise
/// to the right shape.
/// </param>
/// <param name="AnalyzerName">
/// Only meaningful for <see cref="IndexKind.FullText"/>. Names the
/// <see cref="Indexing.Fts.IFullTextAnalyzer"/> used to tokenize both
/// at index-build time and at query time. <see langword="null"/> for
/// non-FTS indexes.
/// </param>
public sealed record IndexDescriptor(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique = false,
    IndexKind Kind = IndexKind.Composite,
    string? AnalyzerName = null);
