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

    /// <summary>
    /// Models registered in the catalog's <c>ModelCatalog</c>, surfaced for
    /// the <c>models.&lt;name&gt;(...)</c> completion namespace. May be
    /// <see langword="null"/> when no model catalog is attached (the offline
    /// JSON manifest workflow doesn't carry models today).
    /// </summary>
    public IReadOnlyList<ModelEntry>? Models { get; init; }

    /// <summary>
    /// User-defined functions registered in the catalog's <c>UdfRegistry</c>,
    /// surfaced for the <c>udf.&lt;name&gt;(...)</c> completion namespace.
    /// May be <see langword="null"/> when the manifest is built without a
    /// live catalog (offline JSON workflow).
    /// </summary>
    public IReadOnlyList<UdfEntry>? Udfs { get; init; }
}

/// <summary>
/// Lightweight description of a registered UDF for completion / hover.
/// Mirrors the subset of <c>UdfDescriptor</c> useful at edit time.
/// </summary>
public sealed class UdfEntry
{
    /// <summary>Unqualified UDF name as it appears after <c>udf.</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Return type name (e.g. <c>"String"</c>, <c>"Int32"</c>), or <see langword="null"/> when none was declared.</summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// <c>"macro"</c> or <c>"procedural"</c> — surfaces as a hint in the
    /// completion popup so editors can show the user which body shape they're
    /// invoking.
    /// </summary>
    public string? BodyKind { get; init; }

    /// <summary>
    /// Whether the UDF was declared <c>PURE</c>. Useful for tooling that
    /// surfaces CSE-eligibility hints.
    /// </summary>
    public bool IsPure { get; init; }

    /// <summary>
    /// Positional parameter shape — name, declared type, and whether a
    /// default makes the parameter optional at the call site.
    /// </summary>
    public IReadOnlyList<ParameterSignature>? Parameters { get; init; }
}

/// <summary>
/// Lightweight description of a registered model for completion / hover.
/// Mirrors the subset of <c>ModelCatalogEntry</c> useful at edit time.
/// </summary>
public sealed class ModelEntry
{
    /// <summary>Stable model identifier as it appears after <c>models.</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Output <c>DataKind</c> name (e.g. <c>"String"</c>, <c>"Float32"</c>).</summary>
    public string? OutputKind { get; init; }

    /// <summary>
    /// Single-valued purpose label from the catalog entry: <c>"llm"</c>,
    /// <c>"classifier"</c>, <c>"detector"</c>, etc.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>Free-form backend identifier (<c>"onnx"</c>, <c>"llama"</c>, …).</summary>
    public string? Backend { get; init; }

    /// <summary>
    /// Human-readable name for hover docs. Distinct from <see cref="Name"/>
    /// (the SQL identifier) — e.g. <c>"MobileNetV2 ImageNet Classifier"</c>.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Positional argument shape: required inputs first, then optional
    /// hyperparameters. Required inputs derive from
    /// <c>ModelCatalogEntry.InputKinds</c>; optionals from
    /// <c>OptionalArgKinds</c>. Empty when the catalog doesn't expose
    /// signature info.
    /// </summary>
    public IReadOnlyList<ParameterSignature>? Parameters { get; init; }
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

    /// <summary>The data kind name (e.g. "Float32", "String", "Vector").</summary>
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

    /// <summary>The return type name (e.g. "Float32", "String"), or null if context-dependent.</summary>
    public string? ReturnType { get; init; }

    /// <summary>A human-readable description of what the function does.</summary>
    public string? Description { get; init; }

    /// <summary>The operational domain this function belongs to (e.g. Temporal, Image, Vector).</summary>
    public FunctionCategory Category { get; init; }

    /// <summary>Whether this is a table-valued function (used in FROM/JOIN) rather than a scalar function.</summary>
    public bool IsTableValued { get; init; }

    /// <summary>Whether this is an aggregate function (used in SELECT with GROUP BY).</summary>
    public bool IsAggregate { get; init; }

    /// <summary>Whether this is a window function (used with OVER clause).</summary>
    public bool IsWindowFunction { get; init; }

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
