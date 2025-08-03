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
    /// <c>ARRAY_MIN</c>, <c>ARRAY_MAX</c>) and correct <c>UNNEST</c> output schemas.
    /// </summary>
    public DataKind? ArrayElementKind { get; }
}
