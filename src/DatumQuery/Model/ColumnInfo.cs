namespace Axon.QueryEngine.Model;

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

    /// <summary>The column name as it appears in query expressions.</summary>
    public string Name { get; }

    /// <summary>The data kind carried by values in this column.</summary>
    public DataKind Kind { get; }

    /// <summary>Whether the column may contain null values.</summary>
    public bool Nullable { get; }
}
