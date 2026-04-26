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
    /// Session <c>search_path</c> at manifest-build time. Drives
    /// unqualified-name resolution in the language server: the
    /// semantic analyzer walks this list when validating
    /// <c>SELECT * FROM foo</c>, and the completion provider uses it
    /// to rank suggestions. Defaults to <c>["public", "system"]</c> so
    /// offline manifests (built without a live catalog) still resolve
    /// the built-in system tables.
    /// </summary>
    public IReadOnlyList<string> SearchPath { get; init; } = new[] { "public", "system" };

    /// <summary>
    /// Models registered in the catalog's <c>ModelCatalog</c>, surfaced for
    /// the <c>models.&lt;name&gt;(...)</c> completion namespace. May be
    /// <see langword="null"/> when no model catalog is attached (the offline
    /// JSON manifest workflow doesn't carry models today).
    /// </summary>
    public IReadOnlyList<ModelEntry>? Models { get; init; }

    /// <summary>
    /// User-defined functions registered in the catalog's <c>UdfRegistry</c>.
    /// Each entry carries a <see cref="UdfEntry.SchemaName"/> — post-S7
    /// UDFs live in real schemas (typically <c>public</c>), and the
    /// completion / hover paths qualify call sites via the session
    /// <see cref="SearchPath"/>. May be <see langword="null"/> when the
    /// manifest is built without a live catalog (offline JSON workflow).
    /// </summary>
    public IReadOnlyList<UdfEntry>? Udfs { get; init; }

    /// <summary>
    /// Stored procedures registered in the catalog's <c>ProcedureRegistry</c>.
    /// Each entry carries a <see cref="ProcedureEntry.SchemaName"/>; calls
    /// resolve through the same search_path walk as UDFs. Procedures
    /// REQUIRE <c>CALL</c> — the language server flags them in expression
    /// position. May be <see langword="null"/> when the manifest is built
    /// without a live catalog.
    /// </summary>
    public IReadOnlyList<ProcedureEntry>? Procedures { get; init; }

    /// <summary>
    /// Function contexts registered in the engine's
    /// <c>FunctionContextRegistry</c>. Each entry carries the context's
    /// name, the canonical lambda parameter list, the optional parent
    /// context, and the list of globally-visible functions the context
    /// "borrows" into its lambda-body scope. Read by the completion
    /// provider when the cursor sits inside a lambda parameter slot whose
    /// declared context is non-null: completion is filtered to the
    /// effective whitelist of that context. <see langword="null"/> when
    /// the manifest is built without context support.
    /// </summary>
    public IReadOnlyList<FunctionContextEntry>? FunctionContexts { get; init; }
}

/// <summary>
/// Manifest-side description of an <c>IFunctionContext</c>. Mirrors the
/// runtime <c>FunctionContextDescriptor</c> at edit time so the LS can
/// reason about lambda scoping without instantiating the engine.
/// </summary>
public sealed class FunctionContextEntry
{
    /// <summary>Context identifier (referenced by per-function <c>Contexts</c> lists and by <c>ParameterSignature.LambdaContextName</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Canonical lambda parameter list — name + kind, in declaration order.</summary>
    public required IReadOnlyList<LambdaParameterEntry> Parameters { get; init; }

    /// <summary>Parent context name for whitelist inheritance, or <see langword="null"/> for a root context.</summary>
    public string? ParentName { get; init; }

    /// <summary>Globally-visible function names this context borrows into its lambda-body scope.</summary>
    public required IReadOnlyList<string> Borrows { get; init; }
}

/// <summary>Single entry in a <see cref="FunctionContextEntry.Parameters"/> list.</summary>
public sealed class LambdaParameterEntry
{
    /// <summary>Canonical parameter name (suggestion used by LS pre-fill).</summary>
    public required string Name { get; init; }

    /// <summary>Parameter kind as a string (e.g. <c>"Float32"</c>).</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// Lightweight description of a registered UDF for completion / hover.
/// Mirrors the subset of <c>UdfDescriptor</c> useful at edit time.
/// </summary>
public sealed class UdfEntry
{
    /// <summary>The schema this UDF lives in. Typically <c>public</c> for user-defined functions.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Unqualified UDF name (combine with <see cref="SchemaName"/> for the canonical identity).</summary>
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
/// <summary>
/// Install-state of a model entry on the current host. Mirrors the
/// <c>residency</c> + <c>status</c> columns on <c>system.models</c> so
/// a single concept governs the runtime introspection view, the language
/// server's completion behaviour, and the install modal's call-to-action.
/// <see cref="Discovered"/> rows appear in <c>models.</c> autocomplete
/// as dimmed suggestions so users can see what the catalog ships before
/// installing; calling one trips parse-time pre-flight and prompts an
/// install.
/// </summary>
public enum ModelInstallStatus
{
    /// <summary>Backend files present + runnable. Native backends (ONNX, LlamaSharp, synthetic) ready to load.</summary>
    Available,
    /// <summary>Anchor file absent on disk. The model is catalogued and registered in the live <c>ModelCatalog</c> but the active version's weights aren't on disk — partial install or post-uninstall anomaly.</summary>
    Missing,
    /// <summary>Files present but requires an external runtime (e.g. Python venv) we can't validate from the catalog alone.</summary>
    Bridge,
    /// <summary>Catalog-declared identifier with no live registration in <c>ModelCatalog</c> — weights have not been downloaded. Surfaces in autocomplete as a dimmed suggestion; calling one trips pre-flight.</summary>
    Discovered,
}

/// <summary>
/// A single model entry surfaced by <see cref="LanguageServerManifest"/>.
/// Carries enough metadata for completions, signature help, and hover
/// rendering — name, install status, output kind, parameter list, and
/// (for struct-returning models) the declared field shape.
/// </summary>
public sealed class ModelEntry
{
    /// <summary>Stable model identifier as it appears after <c>models.</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// On-disk install state. <see cref="ModelInstallStatus.Available"/>
    /// for entries the language server should expose in
    /// <c>models.</c> completion; everything else is hidden from
    /// completion but still surfaces in <c>system.models</c> introspection
    /// and the Model Manager UI.
    /// </summary>
    public ModelInstallStatus Status { get; init; } = ModelInstallStatus.Available;

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

    /// <summary>
    /// For struct-returning models, the ordered field shape of the output
    /// (parsed from <c>RETURNS Struct&lt;…&gt;</c> for SQL-defined models,
    /// or from <c>IModel.OutputFields</c> for built-ins). Lets the language
    /// server resolve <c>model_call().field</c> hovers to the field's
    /// declared kind. <see langword="null"/> for non-struct returns and for
    /// opaque bare <c>RETURNS Struct</c> models.
    /// </summary>
    public IReadOnlyList<StructFieldSignature>? OutputStructFields { get; init; }

    /// <summary>
    /// TaskTypeRegistry contract names the owning catalog entry declares
    /// it implements (e.g. <c>"TextEmbedder"</c>, <c>"DepthEstimatorMetric"</c>).
    /// Sourced from the catalog vocabulary, so engine-only builtins without
    /// a vocabulary entry have this <see langword="null"/>. Used by
    /// completion to surface "what kind of model is this" in the row.
    /// </summary>
    public IReadOnlyList<string>? Tasks { get; init; }

    /// <summary>
    /// Version string currently active on disk for the owning catalog
    /// entry (read from <c>&lt;DATUM_MODELS&gt;/&lt;id&gt;/active</c>).
    /// <see langword="null"/> when the entry has never been installed or
    /// the identifier has no owning catalog entry (engine-only builtins).
    /// Paired with <see cref="LatestVersion"/> to drive the hover drift hint.
    /// </summary>
    public string? ActiveVersion { get; init; }

    /// <summary>
    /// Newest catalog-declared version (the <c>versions[0].version</c> the
    /// catalog ships) for the owning entry. <see langword="null"/> for
    /// engine-only builtins without a catalog entry. Drift = installed
    /// (<see cref="ActiveVersion"/> non-null) AND
    /// <see cref="ActiveVersion"/> != <see cref="LatestVersion"/>.
    /// </summary>
    public string? LatestVersion { get; init; }
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
    /// <summary>
    /// The schema this function is registered under (<c>system</c>,
    /// <c>inference</c>, <c>tokenizer</c>, <c>templates</c>, …). Used by
    /// the completion provider to filter built-ins on
    /// <c>schema.</c>-qualified completions — without it, every function
    /// would surface under every schema or none at all. Defaults to
    /// <c>system</c> for backward compatibility with manifests that
    /// predate schema-aware functions.
    /// </summary>
    public string SchemaName { get; init; } = "system";

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

    /// <summary>
    /// Output columns emitted by a table-valued function, when its output
    /// schema is fixed (independent of argument values). Drives hover on
    /// columns referenced via a TVF source — without it, the language
    /// server has no per-column type info for <c>SELECT a, b FROM tvf(...)</c>.
    /// <see langword="null"/> for scalar functions and for TVFs whose schema
    /// depends on the call site (e.g. <c>range</c>'s column kind follows the
    /// widest argument); those keep falling back to the
    /// <see cref="ReturnType"/> string rendering.
    /// </summary>
    public IReadOnlyList<TableColumnEntry>? OutputColumns { get; init; }

    /// <summary>
    /// Alternative parameter shapes for overloaded functions — the second,
    /// third, … signature variants beyond the primary <see cref="Parameters"/>
    /// list. Used by the semantic analyzer's argument-type validation: if
    /// any shape (primary or any alternative) matches the call's actual
    /// argument kinds, no diagnostic is emitted. Without this the analyzer
    /// would warn on every legitimate use of an overload that wasn't the
    /// first variant declared (e.g. <c>point_cloud_from_depth_pinhole</c>'s
    /// <c>Float32[]</c> depth overload). <see langword="null"/> when the
    /// function has a single variant (the common case).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ParameterSignature>>? AdditionalParameterShapes { get; init; }

    /// <summary>
    /// For struct-returning scalar functions, the ordered field shape of
    /// the output. Same purpose as <see cref="ModelEntry.OutputStructFields"/>
    /// but for any scalar function (most commonly SQL-defined models,
    /// which register as scalar functions). Lets the language server
    /// resolve <c>fn(...).field</c> hovers to the field's declared kind.
    /// <see langword="null"/> for non-struct returns.
    /// </summary>
    public IReadOnlyList<StructFieldSignature>? OutputStructFields { get; init; }

    /// <summary>Whether this is an aggregate function (used in SELECT with GROUP BY).</summary>
    public bool IsAggregate { get; init; }

    /// <summary>Whether this is a window function (used with OVER clause).</summary>
    public bool IsWindowFunction { get; init; }

    /// <summary>
    /// Names of the lambda-body <see cref="FunctionContextEntry"/>s this
    /// function is visible inside. Empty (or <see langword="null"/>) means
    /// "globally visible" — the function resolves in every scope, the
    /// default for every built-in. A non-empty list scopes the function to
    /// lambda bodies whose parameter slot named one of those contexts (or
    /// any descendant context). The completion provider consults this when
    /// filtering by enclosing lambda context.
    /// </summary>
    public IReadOnlyList<string>? Contexts { get; init; }
}

/// <summary>
/// Lightweight description of a registered procedure for completion /
/// hover. Procedures REQUIRE <c>CALL</c> — the language server uses
/// these entries to surface signature info on CALL statements and to
/// flag procedures in expression position.
/// </summary>
public sealed class ProcedureEntry
{
    /// <summary>The schema this procedure lives in.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Unqualified procedure name (combine with <see cref="SchemaName"/> for the canonical identity).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Positional parameter shape — name, declared type, and whether a
    /// default makes the parameter optional at the call site.
    /// </summary>
    public IReadOnlyList<ParameterSignature>? Parameters { get; init; }
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

    /// <summary>
    /// When this parameter's kind is <c>"Lambda"</c>, the name of the
    /// <see cref="FunctionContextEntry"/> the lambda body operates inside.
    /// <see langword="null"/> means either the parameter isn't a lambda or
    /// the lambda is unscoped (callable but inherits surrounding
    /// resolution rules). Drives context-aware completion: when the
    /// cursor sits inside this parameter slot, the LS switches the
    /// completion whitelist to the named context's effective set.
    /// </summary>
    public string? LambdaContextName { get; init; }

    /// <summary>
    /// When this parameter accepts a string drawn from an enumerated set
    /// of values (e.g. <c>blend(content, mode)</c>'s <c>mode</c> takes
    /// <c>'add'</c> / <c>'multiply'</c> / …), the canonical list. The
    /// completion provider surfaces these as suggestions when the cursor
    /// sits inside the string literal at this parameter position.
    /// <see langword="null"/> when the parameter has no enumerated value
    /// set (the default for plain String / numeric / other parameters).
    /// </summary>
    public IReadOnlyList<string>? EnumValues { get; init; }
}

/// <summary>
/// One field in a struct-returning function or model's output shape.
/// <see cref="Kind"/> is the canonical kind string for the field's value —
/// scalar (<c>"Int32"</c>), array (<c>"Array&lt;Float32&gt;"</c>), or
/// nested struct (<c>"Struct&lt;…&gt;"</c>). Drives the LanguageServer's
/// <c>fn(...).field</c> hover / completion resolution.
/// </summary>
public sealed class StructFieldSignature
{
    /// <summary>The field name as declared in the <c>RETURNS Struct&lt;…&gt;</c> annotation or <c>IModel.OutputFields</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Canonical kind string for the field's value (e.g. <c>"Int32"</c>, <c>"Array&lt;Float32&gt;"</c>).</summary>
    public required string Kind { get; init; }
}
