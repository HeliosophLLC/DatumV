namespace DatumIngest.Web.Dtos.Functions;

/// <summary>
/// Response payload for <c>GET /api/functions/models</c>: every SQL-defined
/// model (registered via <c>CREATE MODEL</c>) with its parameter metadata,
/// USING binding, and IMPLEMENTS task contract.
/// </summary>
/// <remarks>
/// Distinct from <c>system.models</c> built-in inference entries — this
/// endpoint surfaces only the procedural-body models the user (or catalog
/// installer) declared through SQL. Both flavours could share an endpoint
/// in a future iteration; for now this is the focused surface for the
/// function-executor UI's "registered models" picker.
/// </remarks>
public sealed record ModelListResponse(
    IReadOnlyList<ModelDto> Models);

/// <summary>
/// One SQL-defined model in the catalog.
/// </summary>
/// <param name="Schema">Schema the model lives in. Always <c>"models"</c> today (CREATE MODEL enforces it).</param>
/// <param name="Name">Unqualified model name.</param>
/// <param name="Parameters">Declared parameters in order, with full per-parameter metadata.</param>
/// <param name="ReturnType">
/// The <c>RETURNS</c> annotation. Always non-null for models — the parser
/// rejects a CREATE MODEL without one.
/// </param>
/// <param name="ReturnIsNotNull">
/// <see langword="true"/> when the return type was declared with <c>IS NOT NULL</c>.
/// </param>
/// <param name="UsingPath">
/// Raw path supplied to the <c>USING</c> clause — the relative or
/// <c>file://</c>-prefixed string the author wrote. Pair with
/// <see cref="ResolvedUsingPath"/> when displaying both the source form
/// and the absolute file location.
/// </param>
/// <param name="ResolvedUsingPath">
/// Absolute path the registrar resolved <see cref="UsingPath"/> to against
/// the host's models directory. Useful for "open in file explorer" affordances
/// and for displaying which on-disk bundle is actually bound.
/// </param>
/// <param name="ImplementsTask">
/// Optional <c>IMPLEMENTS TaskName</c> task-contract declaration
/// (e.g. <c>"LabeledObjectDetector"</c>). Lets the front-end route models
/// into capability-shaped pickers ("any labeled object detector"). <see langword="null"/>
/// when no <c>IMPLEMENTS</c> clause was declared.
/// </param>
/// <param name="SourceText">
/// Original <c>CREATE MODEL</c> SQL captured verbatim. Useful for displaying
/// the body to power users and for round-tripping the registration through
/// the catalog persistence layer.
/// </param>
public sealed record ModelDto(
    string Schema,
    string Name,
    IReadOnlyList<ScalarFunctionParameterDto> Parameters,
    string ReturnType,
    bool ReturnIsNotNull,
    string UsingPath,
    string ResolvedUsingPath,
    string? ImplementsTask,
    string? SourceText);
