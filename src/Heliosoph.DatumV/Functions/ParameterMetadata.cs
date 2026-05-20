namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Per-parameter metadata surfaced to UI clients (function-executor forms,
/// DAG-node configuration sidebars, language-server hover hints). Shared
/// shape between built-in functions (via <see cref="ParameterSpec"/>) and
/// SQL-defined UDFs / models (via <c>UdfParameter</c>) so consumers don't
/// have to branch on parameter origin.
/// </summary>
/// <remarks>
/// <para>
/// All fields are optional. A parameter with no metadata is valid; the
/// UI falls back to type-driven defaults (e.g. Float32 with no range
/// renders as a spinbox; String with no enum renders as a text input).
/// </para>
/// <para>
/// <strong>Constraints are typed, not stringly-typed.</strong> See
/// <see cref="ParameterCheck"/> for the closed-set of canonical constraint
/// shapes plus the <c>CustomCheck</c> escape hatch for arbitrary SQL
/// boolean expressions. Authors construct them directly:
/// <code>
/// Metadata: new ParameterMetadata(
///     Check: new BetweenCheck(0.0m, 1.0m),
///     Step: 0.05m,
///     Description: "Sigmoid-space threshold.")
/// </code>
/// </para>
/// <para>
/// <strong>Decimal for numeric values.</strong> <see cref="Step"/> and the
/// numeric bounds inside <see cref="ParameterCheck"/> subclasses use
/// <see cref="decimal"/> to preserve author intent verbatim through
/// catalog persistence and SQL pretty-printing. See
/// <see cref="ParameterCheck"/> for the full rationale.
/// </para>
/// </remarks>
/// <param name="Check">
/// Structured constraint on the parameter value. <c>null</c> means no
/// declared constraint; the UI infers a default widget from the parameter
/// type.
/// </param>
/// <param name="Step">
/// UI slider/spinbox step granularity. Decimal so common values like
/// <c>0.05m</c> stay exact through serialization. <c>null</c> means
/// "let the UI infer from the constraint and type."
/// </param>
/// <param name="Unit">
/// Display-only unit string (<c>"pixels"</c>, <c>"seconds"</c>,
/// <c>"%"</c>, <c>"px"</c>). Rendered as a suffix on labels / tooltips.
/// </param>
/// <param name="Description">
/// One-line parameter description for tooltips and hover hints. The
/// function-level description lives on <see cref="FunctionDescriptor"/>;
/// this is the per-parameter docstring.
/// </param>
public sealed record ParameterMetadata(
    ParameterCheck? Check = null,
    decimal? Step = null,
    string? Unit = null,
    string? Description = null);
