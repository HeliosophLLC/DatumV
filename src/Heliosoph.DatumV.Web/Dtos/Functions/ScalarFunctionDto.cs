namespace Heliosoph.DatumV.Web.Dtos.Functions;

/// <summary>
/// Response payload for <c>GET /api/functions/scalar</c>: a flat list of
/// every registered scalar function with full signature metadata, suitable
/// for rendering an Execute-Function form on the client.
/// </summary>
public sealed record ScalarFunctionListResponse(
    IReadOnlyList<ScalarFunctionDto> Functions);

/// <summary>
/// One scalar function in the catalog.
/// </summary>
/// <param name="Schema">SQL schema the function lives under (<c>system</c>, <c>inference</c>, …).</param>
/// <param name="Name">Canonical case-insensitive name.</param>
/// <param name="Aliases">Additional names registered for the same function.</param>
/// <param name="Category">Functional category as named by <c>FunctionCategory</c>.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="BodyScope">
/// Procedural context required for the function. <c>None</c> means callable
/// anywhere; non-<c>None</c> values (e.g. <c>ModelBody</c>) mean the function
/// only makes sense inside that scope and should be hidden from the
/// general-purpose Execute-Function picker.
/// </param>
/// <param name="Signatures">Accepted argument shapes.</param>
public sealed record ScalarFunctionDto(
    string Schema,
    string Name,
    IReadOnlyList<string> Aliases,
    string Category,
    string Description,
    string BodyScope,
    IReadOnlyList<ScalarFunctionSignatureDto> Signatures);

/// <summary>
/// One accepted argument shape for a function.
/// </summary>
/// <param name="Parameters">Fixed positional parameters (may be empty).</param>
/// <param name="Variadic">Optional trailing variadic; null when absent.</param>
/// <param name="ReturnType">Rule that produces the result kind.</param>
public sealed record ScalarFunctionSignatureDto(
    IReadOnlyList<ScalarFunctionParameterDto> Parameters,
    ScalarFunctionVariadicDto? Variadic,
    ScalarFunctionReturnTypeDto ReturnType);

/// <summary>
/// One fixed positional parameter slot.
/// </summary>
/// <param name="Name">Parameter name as declared by the function.</param>
/// <param name="KindLabel">
/// Human-readable label for the accepted kind set
/// (<c>"Int32"</c>, <c>"Numeric"</c>, <c>"one of Int32, Int64"</c>, …).
/// </param>
/// <param name="AcceptedKinds">
/// Concrete list of <c>DataKind</c> names this slot accepts, enumerated by
/// brute force from the runtime matcher. Empty when the matcher accepts
/// every kind. Lets the client decide which input control to render
/// without re-implementing the matcher rules.
/// </param>
/// <param name="AcceptsAnyKind">
/// True when the parameter is fully polymorphic (<c>DataKindMatcher.Any</c>).
/// <see cref="AcceptedKinds"/> stays empty in this case to avoid bloating
/// the wire payload with every <c>DataKind</c>.
/// </param>
/// <param name="IsOptional">When true, the parameter may be omitted by the caller.</param>
/// <param name="ArrayMatch">
/// One of <c>"Either"</c>, <c>"Scalar"</c>, <c>"Array"</c> — scalar vs typed-
/// array filter on the slot. <c>Either</c> is the default.
/// </param>
/// <param name="DefaultExpression">
/// Verbatim SQL text of the default expression when the parameter is
/// optional and has a default value (e.g. <c>"CAST(0.25 AS Float32)"</c>).
/// <c>null</c> for required parameters and for scalar functions that
/// don't currently support defaults. The client renders this as
/// display-only — it doesn't evaluate the expression.
/// </param>
/// <param name="Check">
/// Structured constraint on the parameter value (typed discriminated
/// union — see <see cref="ParameterCheckDto"/>). Clients dispatch on the
/// <c>kind</c> discriminator to pick a rendering widget: <c>"between"</c>
/// → range slider, <c>"in"</c> → dropdown, <c>"custom"</c> → text input
/// with server-side validation. <c>null</c> when no constraint is
/// declared.
/// </param>
/// <param name="Step">
/// UI slider/spinbox granularity hint. Decimal so common values like
/// <c>0.05</c> stay exact on the server side (JS clients will round to
/// double anyway). <c>null</c> means "infer from constraint and type."
/// </param>
/// <param name="Unit">
/// Display-only unit suffix (<c>"pixels"</c>, <c>"seconds"</c>, <c>"%"</c>).
/// <c>null</c> when the parameter is unitless.
/// </param>
/// <param name="Description">
/// Per-parameter one-line description for tooltips and hover hints.
/// <c>null</c> when no description was declared.
/// </param>
public sealed record ScalarFunctionParameterDto(
    string Name,
    string KindLabel,
    IReadOnlyList<string> AcceptedKinds,
    bool AcceptsAnyKind,
    bool IsOptional,
    string ArrayMatch,
    string? DefaultExpression = null,
    ParameterCheckDto? Check = null,
    decimal? Step = null,
    string? Unit = null,
    string? Description = null);

/// <summary>
/// Trailing variadic specification.
/// </summary>
/// <param name="Name">Variadic group name.</param>
/// <param name="KindLabel">Human-readable kind label.</param>
/// <param name="AcceptedKinds">Concrete accepted kinds; empty for fully-polymorphic.</param>
/// <param name="AcceptsAnyKind">True when matcher is <c>Any</c>.</param>
/// <param name="MinOccurrences">Minimum count of variadic arguments.</param>
/// <param name="RequireSameKindAcrossArgs">When true, every variadic arg must share a kind.</param>
/// <param name="ArrayMatch">Per-argument scalar/array filter; one of <c>"Either"</c>, <c>"Scalar"</c>, <c>"Array"</c>.</param>
public sealed record ScalarFunctionVariadicDto(
    string Name,
    string KindLabel,
    IReadOnlyList<string> AcceptedKinds,
    bool AcceptsAnyKind,
    int MinOccurrences,
    bool RequireSameKindAcrossArgs,
    string ArrayMatch);

/// <summary>
/// Description of a function's return type.
/// </summary>
/// <param name="Description">
/// Human-readable label: a <c>DataKind</c> name (<c>"Int32"</c>), or a rule
/// description for runtime-typed returns (<c>"same as argument 0"</c>,
/// <c>"Array&lt;Float32&gt;"</c>).
/// </param>
/// <param name="StaticHint">
/// The result kind when known without seeing arguments, otherwise null.
/// Lets the Execute-Function pane decide whether the result viewer can be
/// pre-configured (<c>"Image"</c> → image preview) or must wait for the
/// first row to inspect cell kinds (<c>null</c>).
/// </param>
/// <param name="ProducesArray">True when the result is a typed array.</param>
public sealed record ScalarFunctionReturnTypeDto(
    string Description,
    string? StaticHint,
    bool ProducesArray);
