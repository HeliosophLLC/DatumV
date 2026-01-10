using DatumIngest.Parsing.Ast;

namespace DatumIngest.Model;

/// <summary>
/// Describes a single column within a <see cref="Schema"/>: its name, data kind, and nullability.
/// Uses an explicit constructor so that parameter names (lowercase) can differ from
/// property names (PascalCase), matching both call-site named arguments and property access.
/// </summary>
public sealed record ColumnInfo
{
    /// <summary>Creates a column descriptor.</summary>
    /// <param name="name">The column name as it appears in query expressions.</param>
    /// <param name="kind">
    /// The data kind carried by values in this column. For typed-array columns
    /// this is the per-element kind; combine with the <see cref="IsArray"/>
    /// init flag (e.g. <c>new ColumnInfo(name, DataKind.Float32, nullable) { IsArray = true }</c>).
    /// </param>
    /// <param name="nullable">Whether the column may contain null values.</param>
    public ColumnInfo(string name, DataKind kind, bool nullable)
    {
        Name = name;
        Kind = kind;
        Nullable = nullable;
    }

    /// <summary>
    /// Creates a column descriptor for a <see cref="DataKind.Struct"/> column.
    /// Field metadata is provided as an ordered list of child <see cref="ColumnInfo"/>
    /// descriptors, shared across all rows — no per-value allocation.
    /// </summary>
    /// <param name="name">The column name as it appears in query expressions.</param>
    /// <param name="nullable">Whether the column may contain null values.</param>
    /// <param name="fields">Ordered field descriptors for the struct's named fields.</param>
    public ColumnInfo(string name, bool nullable, IReadOnlyList<ColumnInfo> fields)
    {
        Name = name;
        Kind = DataKind.Struct;
        Nullable = nullable;
        Fields = fields;
    }

    /// <summary>The column name as it appears in query expressions.</summary>
    public string Name { get; }

    /// <summary>The data kind carried by values in this column.</summary>
    public DataKind Kind { get; }

    /// <summary>Whether the column may contain null values.</summary>
    public bool Nullable { get; }

    /// <summary>
    /// For <see cref="DataKind.Struct"/> columns, the ordered list of named field descriptors.
    /// <c>null</c> when <see cref="Kind"/> is not <see cref="DataKind.Struct"/> or when
    /// the struct schema is not known at plan time.
    /// Shared across all rows — field metadata is never allocated per-value.
    /// </summary>
    public IReadOnlyList<ColumnInfo>? Fields { get; }

    /// <summary>
    /// True when this column holds typed arrays of <see cref="Kind"/> elements
    /// (e.g. byte arrays as <see cref="DataKind.UInt8"/> + <see cref="IsArray"/>=true,
    /// integer arrays as <see cref="DataKind.Int32"/> + <see cref="IsArray"/>=true).
    /// Defaults to <c>false</c>; set with object-initializer syntax:
    /// <c>new ColumnInfo(name, DataKind.UInt8, nullable) { IsArray = true }</c>.
    /// Per-element kind is <see cref="Kind"/> directly — there is no separate
    /// element-kind field, the <c>IsArray</c> flag is the only array marker.
    /// </summary>
    public bool IsArray { get; init; }

    /// <summary>
    /// Convenience: true when this column is a byte-array column —
    /// <see cref="Kind"/> is <see cref="DataKind.UInt8"/> and
    /// <see cref="IsArray"/> is set. Mirrors <see cref="DataValue.IsByteArrayKind"/>.
    /// </summary>
    public bool IsByteArrayColumn => Kind == DataKind.UInt8 && IsArray;

    /// <summary>
    /// Optional <c>DEFAULT</c> literal expression captured from the column's
    /// <c>CREATE TABLE</c> definition. Restricted to <see cref="LiteralExpression"/>
    /// at registration time — the catalog rejects non-literal defaults so the
    /// expression here is always a constant the INSERT layer can evaluate
    /// once and reuse for every absent row value. <see langword="null"/>
    /// when the column has no default.
    /// </summary>
    public Expression? DefaultExpression { get; init; }

    /// <summary>
    /// Optional <c>IDENTITY(seed, step)</c> spec captured from the
    /// column's <c>CREATE TABLE</c> definition. <see langword="null"/>
    /// for non-IDENTITY columns; non-null implies the catalog auto-fills
    /// the column at INSERT time and rejects any explicit value supplied
    /// for it (PostgreSQL <c>GENERATED ALWAYS</c> semantics). At most
    /// one column per table may carry this — enforced at <c>CREATE TABLE</c>
    /// time by the catalog.
    /// </summary>
    public IdentitySpec? Identity { get; init; }

    /// <summary>
    /// True when this column is part of the table's PRIMARY KEY. The
    /// canonical ordered list of PK columns lives on
    /// <see cref="Schema.PrimaryKeyColumnIndices"/>; this flag is the
    /// per-column convenience for "is this column in the PK?". Columns
    /// with this flag are implicitly NOT NULL (catalog auto-promotes
    /// at <c>CREATE TABLE</c> time).
    /// </summary>
    public bool IsPrimaryKey { get; init; }
}
