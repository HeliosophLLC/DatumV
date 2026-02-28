using Tapper;

namespace DatumIngest.Web.Hubs;

/// <summary>
/// Discriminator on <see cref="CatalogChangedEvent"/>. One value per
/// <see cref="DatumIngest.Catalog.CatalogEvents"/> channel so client code
/// can switch on the change without inspecting payload shape.
/// </summary>
[TranspilationSource]
public enum CatalogChangeKind
{
    SchemaCreated,
    SchemaDropped,

    TableCreated,
    TableAltered,
    TableDropped,

    IndexCreated,
    IndexDropped,

    FunctionCreated,
    FunctionAltered,
    FunctionDropped,

    ProcedureCreated,
    ProcedureAltered,
    ProcedureDropped,

    ModelCreated,
    ModelAltered,
    ModelDropped,
}

/// <summary>
/// Lean SignalR push payload for catalog mutations. One event per DDL
/// commit, regardless of payload kind — clients switch on
/// <see cref="Kind"/> and re-fetch detail from REST when they need it.
/// </summary>
/// <remarks>
/// Intentionally minimal: name + schema + an optional <see cref="ChildName"/>
/// for index events (where the index name is distinct from the table name).
/// We deliberately don't ship column lists, descriptor blobs, or signature
/// strings here — those go over <c>/api/lang/*</c> when a consumer asks. The
/// push is an invalidation signal, not a state replica. If/when a consumer
/// needs richer payloads we'll add a parallel "thick" channel rather than
/// fattening this one.
/// </remarks>
/// <param name="Kind">Discriminator. See <see cref="CatalogChangeKind"/>.</param>
/// <param name="Schema">Schema component of the qualified name. Null for schema-level events where <see cref="Name"/> already holds the schema name.</param>
/// <param name="Name">Primary entity name. For schema events this is the schema; for everything else this is the unqualified entity name within <see cref="Schema"/>.</param>
/// <param name="ChildName">Child entity name when applicable (index name for index events). Null otherwise.</param>
[TranspilationSource]
public sealed record CatalogChangedEvent(
    CatalogChangeKind Kind,
    string? Schema,
    string Name,
    string? ChildName);
