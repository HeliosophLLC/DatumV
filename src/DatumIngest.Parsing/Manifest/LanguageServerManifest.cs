namespace DatumIngest.Manifest;

/// <summary>
/// A pre-built manifest that provides everything a language server needs for
/// SQL autocomplete, diagnostics, and hover — without runtime access to data files.
/// </summary>
public sealed class LanguageServerManifest
{
    /// <summary>Schema format version for forward compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Table schemas resolved from the data catalog.</summary>
    public required IReadOnlyList<TableSchemaEntry> Tables { get; init; }

    /// <summary>Function signatures for all registered scalar and table-valued functions.</summary>
    public required IReadOnlyList<FunctionSignature> Functions { get; init; }

    /// <summary>SQL keywords recognized by the DatumIngest SQL dialect.</summary>
    public required IReadOnlyList<string> Keywords { get; init; }
}

/// <summary>
/// Schema information for a single table: its name and the columns it exposes.
/// </summary>
public sealed class TableSchemaEntry
{
    /// <summary>The logical table name as used in FROM/JOIN clauses.</summary>
    public required string Name { get; init; }

    /// <summary>The columns available in this table.</summary>
    public required IReadOnlyList<TableColumnEntry> Columns { get; init; }
}

/// <summary>
/// A single column within a table schema entry.
/// </summary>
public sealed class TableColumnEntry
{
    /// <summary>The column name as it appears in query expressions.</summary>
    public required string Name { get; init; }

    /// <summary>The data kind name (e.g. "Scalar", "String", "Vector").</summary>
    public required string Kind { get; init; }

    /// <summary>Whether this column may contain null values.</summary>
    public required bool Nullable { get; init; }
}

/// <summary>
/// Describes a function signature for autocomplete and hover display.
/// </summary>
public sealed class FunctionSignature
{
    /// <summary>The function name as used in SQL expressions.</summary>
    public required string Name { get; init; }

    /// <summary>The ordered parameter list for this signature.</summary>
    public required IReadOnlyList<ParameterSignature> Parameters { get; init; }

    /// <summary>The return type name (e.g. "Scalar", "String"), or null if context-dependent.</summary>
    public string? ReturnType { get; init; }

    /// <summary>A human-readable description of what the function does.</summary>
    public string? Description { get; init; }

    /// <summary>Whether this is a table-valued function (used in FROM/JOIN) rather than a scalar function.</summary>
    public bool IsTableValued { get; init; }

    /// <summary>The base query-unit cost per invocation, as reported by the function implementation.</summary>
    public int QueryUnitCost { get; init; }
}

/// <summary>
/// Describes a single parameter within a function signature.
/// </summary>
public sealed class ParameterSignature
{
    /// <summary>The parameter name for display (e.g. "value", "start", "length").</summary>
    public required string Name { get; init; }

    /// <summary>The expected data kind name, or "Any" if the parameter accepts any type.</summary>
    public required string Kind { get; init; }

    /// <summary>Whether this parameter is optional.</summary>
    public bool IsOptional { get; init; }
}
