namespace Heliosoph.DatumV.Web.Dtos.Functions;

/// <summary>
/// Response payload for <c>GET /api/functions/udfs</c>: every SQL UDF
/// registered against the catalog with its full parameter metadata
/// (Check / Step / Unit / Description) and body-shape discriminator.
/// </summary>
public sealed record UdfListResponse(
    IReadOnlyList<UdfDto> Udfs);

/// <summary>
/// One user-defined function in the catalog.
/// </summary>
/// <param name="Schema">SQL schema the UDF lives in (<c>public</c>, <c>analytics</c>, …).</param>
/// <param name="Name">Unqualified UDF name.</param>
/// <param name="BodyKind">
/// <c>"macro"</c> when the body is an inline expression substituted at plan time,
/// <c>"procedural"</c> when the body is a <c>BEGIN…END</c> statement sequence
/// executed by a runtime adapter. The two shapes are call-site-identical but
/// differ in enforcement: <see cref="ScalarFunctionParameterDto.Check"/> is
/// enforced at runtime only for procedural UDFs (macros surface it for the
/// catalog UI but the inliner substitutes the parameter at plan time).
/// </param>
/// <param name="IsPure">
/// <see langword="true"/> when the UDF was declared <c>PURE</c>. CSE may
/// consolidate identical procedural-UDF call sites when this is set; pure
/// macros are inlined regardless.
/// </param>
/// <param name="Parameters">Declared parameters in order, with full per-parameter metadata.</param>
/// <param name="ReturnType">
/// The <c>RETURNS</c> annotation. <see langword="null"/> for macro UDFs without
/// an explicit return-type annotation (the inlined body's natural type wins).
/// </param>
/// <param name="ReturnIsNotNull">
/// <see langword="true"/> when the return type was declared with <c>IS NOT NULL</c> —
/// a NULL return raises a runtime error.
/// </param>
/// <param name="SourceText">
/// Original <c>CREATE FUNCTION</c> SQL captured verbatim. Reflects the
/// user's authored formatting; the engine reparses this on catalog reload.
/// <see langword="null"/> only when the UDF was registered through an AST-only
/// path (rare; tests).
/// </param>
public sealed record UdfDto(
    string Schema,
    string Name,
    string BodyKind,
    bool IsPure,
    IReadOnlyList<ScalarFunctionParameterDto> Parameters,
    string? ReturnType,
    bool ReturnIsNotNull,
    string? SourceText);

/// <summary>
/// Response payload for <c>GET /api/functions/procedures</c>: every SQL
/// procedure registered against the catalog via <c>CREATE PROCEDURE</c>.
/// Procedures aren't expression-callable (only <c>CALL</c> invokes them)
/// so they live in a separate response from UDFs even though the metadata
/// shape is similar.
/// </summary>
public sealed record ProcedureListResponse(
    IReadOnlyList<ProcedureDto> Procedures);

/// <summary>
/// One stored procedure in the catalog.
/// </summary>
public sealed record ProcedureDto(
    string Schema,
    string Name,
    IReadOnlyList<ScalarFunctionParameterDto> Parameters,
    string? SourceText);
