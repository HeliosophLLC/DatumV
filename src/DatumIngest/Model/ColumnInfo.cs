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
    /// <param name="kind">The data kind carried by values in this column.</param>
    /// <param name="nullable">Whether the column may contain null values.</param>
    public ColumnInfo(string name, DataKind kind, bool nullable)
    {
        Name = name;
        Kind = kind;
        Nullable = nullable;
    }

    /// <summary>
    /// Creates a column descriptor with array element kind metadata.
    /// Used for <see cref="DataKind.Array"/> columns where the element kind is
    /// known at plan time, enabling element-kind-aware function type inference.
    /// </summary>
    /// <param name="name">The column name as it appears in query expressions.</param>
    /// <param name="kind">The data kind carried by values in this column.</param>
    /// <param name="nullable">Whether the column may contain null values.</param>
    /// <param name="arrayElementKind">The element kind for <see cref="DataKind.Array"/> columns, or <c>null</c> for all other kinds.</param>
    public ColumnInfo(string name, DataKind kind, bool nullable, DataKind? arrayElementKind)
    {
        Name = name;
        Kind = kind;
        Nullable = nullable;
        ArrayElementKind = arrayElementKind;
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
    /// For <see cref="DataKind.Array"/> columns, the element kind of the array.
    /// <c>null</c> when the element kind is unknown at plan time or when
    /// <see cref="Kind"/> is not <see cref="DataKind.Array"/>.
    /// Enables element-kind-aware function type inference (e.g. <c>ARRAY_GET</c>,
    /// <c>ARRAY_MIN</c>, <c>ARRAY_MAX</c>).
    /// </summary>
    public DataKind? ArrayElementKind { get; }

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
    /// Independent of <see cref="ArrayElementKind"/>, which only applies to
    /// heterogeneous <see cref="DataKind.Array"/> columns.
    /// </summary>
    public bool IsArray { get; init; }

    /// <summary>
    /// Convenience: true when this column is a byte-array column —
    /// <see cref="Kind"/> is <see cref="DataKind.UInt8"/> and
    /// <see cref="IsArray"/> is set. Mirrors <see cref="DataValue.IsByteArrayKind"/>.
    /// </summary>
    public bool IsByteArrayColumn => Kind == DataKind.UInt8 && IsArray;
}
