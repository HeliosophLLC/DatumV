using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Catalog;

// Catalog-change event types. Each DDL statement that mutates the catalog
// fires exactly one event from this surface, raised on the DDL thread
// AFTER the registry mutation commits. Subscribers should be fast and
// non-blocking — the LSP manifest update is a dictionary swap; SignalR
// rebroadcast queues a message and returns. Anything heavy belongs on a
// background queue, not inline in the event handler.
//
// Convention: `Before` is null on Created, `After` is null on Dropped,
// both are non-null on Altered. `SourceText` is the verbatim SQL slice
// the catalog dispatch captured for round-tripping; null when the
// statement was built programmatically.
//
// Parent/child cascade rule: when a parent entity is dropped (e.g. DROP
// TABLE), the registrar fires the parent event only — NOT one event per
// implicit child (indexes, constraints). Subscribers treat a parent-drop
// event as "blow away this entire subtree." Child events fire only when
// the parent is stable and the child changes in isolation.

/// <summary>Raised after <c>CREATE SCHEMA</c> commits.</summary>
/// <param name="SchemaName">Name of the new schema (case-insensitive).</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record SchemaCreatedEvent(string SchemaName, string? SourceText);

/// <summary>Raised after <c>DROP SCHEMA</c> commits.</summary>
/// <param name="SchemaName">Name of the dropped schema.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record SchemaDroppedEvent(string SchemaName, string? SourceText);

/// <summary>Raised after <c>CREATE TABLE</c> commits.</summary>
/// <param name="Name">Qualified table name.</param>
/// <param name="After">Column schema of the newly created table.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record TableCreatedEvent(QualifiedName Name, Schema After, string? SourceText);

/// <summary>Raised after <c>ALTER TABLE</c> (ADD/DROP COLUMN, ADD/DROP CONSTRAINT, ALTER COLUMN …) commits.</summary>
/// <param name="Name">Qualified table name.</param>
/// <param name="Before">Column schema immediately before the mutation; null if the catalog can't recover it cheaply.</param>
/// <param name="After">Column schema immediately after the mutation.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record TableAlteredEvent(QualifiedName Name, Schema? Before, Schema After, string? SourceText);

/// <summary>Raised after <c>DROP TABLE</c> commits. Implicit child drops (indexes / constraints) do NOT fire their own events.</summary>
/// <param name="Name">Qualified table name.</param>
/// <param name="Before">Column schema of the dropped table; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record TableDroppedEvent(QualifiedName Name, Schema? Before, string? SourceText);

/// <summary>Raised after <c>CREATE INDEX</c> commits on a stable table.</summary>
/// <param name="TableName">Qualified table the index belongs to.</param>
/// <param name="After">Descriptor of the newly created index.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record IndexCreatedEvent(QualifiedName TableName, IndexDescriptor After, string? SourceText);

/// <summary>Raised after <c>DROP INDEX</c> commits on a stable table.</summary>
/// <param name="TableName">Qualified table the index belonged to.</param>
/// <param name="Before">Descriptor of the dropped index; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record IndexDroppedEvent(QualifiedName TableName, IndexDescriptor? Before, string? SourceText);

/// <summary>Raised after <c>CREATE FUNCTION</c> commits when no descriptor existed at the qualified name.</summary>
/// <param name="Name">Qualified function name.</param>
/// <param name="After">Descriptor of the newly created function.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record FunctionCreatedEvent(QualifiedName Name, UdfDescriptor After, string? SourceText);

/// <summary>Raised after <c>CREATE OR REPLACE FUNCTION</c> commits over an existing descriptor.</summary>
/// <param name="Name">Qualified function name.</param>
/// <param name="Before">Descriptor immediately before the replacement.</param>
/// <param name="After">Descriptor immediately after the replacement.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record FunctionAlteredEvent(QualifiedName Name, UdfDescriptor? Before, UdfDescriptor After, string? SourceText);

/// <summary>Raised after <c>DROP FUNCTION</c> commits.</summary>
/// <param name="Name">Qualified function name.</param>
/// <param name="Before">Descriptor of the dropped function; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record FunctionDroppedEvent(QualifiedName Name, UdfDescriptor? Before, string? SourceText);

/// <summary>Raised after <c>CREATE PROCEDURE</c> commits when no descriptor existed at the qualified name.</summary>
/// <param name="Name">Qualified procedure name.</param>
/// <param name="After">Descriptor of the newly created procedure.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ProcedureCreatedEvent(QualifiedName Name, ProcedureDescriptor After, string? SourceText);

/// <summary>Raised after <c>CREATE OR REPLACE PROCEDURE</c> commits over an existing descriptor.</summary>
/// <param name="Name">Qualified procedure name.</param>
/// <param name="Before">Descriptor immediately before the replacement.</param>
/// <param name="After">Descriptor immediately after the replacement.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ProcedureAlteredEvent(QualifiedName Name, ProcedureDescriptor? Before, ProcedureDescriptor After, string? SourceText);

/// <summary>Raised after <c>DROP PROCEDURE</c> commits.</summary>
/// <param name="Name">Qualified procedure name.</param>
/// <param name="Before">Descriptor of the dropped procedure; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ProcedureDroppedEvent(QualifiedName Name, ProcedureDescriptor? Before, string? SourceText);

/// <summary>Raised after <c>CREATE MODEL</c> commits when no descriptor existed at the qualified name.</summary>
/// <param name="Name">Qualified model name.</param>
/// <param name="After">Descriptor of the newly created model.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ModelCreatedEvent(QualifiedName Name, ModelDescriptor After, string? SourceText);

/// <summary>Raised after <c>CREATE OR REPLACE MODEL</c> commits over an existing descriptor.</summary>
/// <param name="Name">Qualified model name.</param>
/// <param name="Before">Descriptor immediately before the replacement.</param>
/// <param name="After">Descriptor immediately after the replacement.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ModelAlteredEvent(QualifiedName Name, ModelDescriptor? Before, ModelDescriptor After, string? SourceText);

/// <summary>Raised after <c>DROP MODEL</c> commits.</summary>
/// <param name="Name">Qualified model name.</param>
/// <param name="Before">Descriptor of the dropped model; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ModelDroppedEvent(QualifiedName Name, ModelDescriptor? Before, string? SourceText);

/// <summary>Raised after <c>CREATE VIEW</c> commits when no descriptor existed at the qualified name.</summary>
/// <param name="Name">Qualified view name.</param>
/// <param name="After">Descriptor of the newly created view.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ViewCreatedEvent(QualifiedName Name, ViewDescriptor After, string? SourceText);

/// <summary>Raised after <c>CREATE OR REPLACE VIEW</c> commits over an existing descriptor.</summary>
/// <param name="Name">Qualified view name.</param>
/// <param name="Before">Descriptor immediately before the replacement.</param>
/// <param name="After">Descriptor immediately after the replacement.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ViewAlteredEvent(QualifiedName Name, ViewDescriptor? Before, ViewDescriptor After, string? SourceText);

/// <summary>Raised after <c>DROP VIEW</c> commits.</summary>
/// <param name="Name">Qualified view name.</param>
/// <param name="Before">Descriptor of the dropped view; null if it couldn't be captured pre-drop.</param>
/// <param name="SourceText">Verbatim SQL slice; null when built programmatically.</param>
public sealed record ViewDroppedEvent(QualifiedName Name, ViewDescriptor? Before, string? SourceText);

/// <summary>
/// Per-catalog event bus. One instance hangs off each <see cref="TableCatalog"/>
/// via <see cref="TableCatalog.Events"/>. Subscribers attach to whichever typed
/// event they care about; the registrar dispatches synchronously on the DDL
/// thread once the underlying registry mutation has committed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subscriber discipline.</strong> Handlers run on the thread that
/// applied the DDL — for a SQL session, that's the planner/executor. Keep
/// handlers fast: a registry diff, a queue-enqueue, a SignalR
/// <c>HubContext.Clients.All.SendAsync</c> are all fine. I/O, locks, or
/// anything that can block the DDL caller don't belong here.
/// </para>
/// <para>
/// <strong>Event ordering.</strong> Events fire only on success — if the
/// underlying <c>Apply…</c> throws, control jumps out before the raise, so
/// subscribers never see an event for a mutation that didn't commit.
/// </para>
/// <para>
/// <strong>Threading.</strong> The bus itself is just a set of typed C#
/// events. <c>+=</c> and <c>-=</c> from any thread are safe (delegate
/// combine is atomic). Subscribers should expect raises from arbitrary
/// threads since DDL can run on any thread the host gives the catalog.
/// </para>
/// </remarks>
public sealed class CatalogEvents
{
    /// <summary>Raised after <c>CREATE SCHEMA</c> commits.</summary>
    public event Action<SchemaCreatedEvent>? SchemaCreated;

    /// <summary>Raised after <c>DROP SCHEMA</c> commits.</summary>
    public event Action<SchemaDroppedEvent>? SchemaDropped;

    /// <summary>Raised after <c>CREATE TABLE</c> commits.</summary>
    public event Action<TableCreatedEvent>? TableCreated;

    /// <summary>Raised after <c>ALTER TABLE</c> commits.</summary>
    public event Action<TableAlteredEvent>? TableAltered;

    /// <summary>Raised after <c>DROP TABLE</c> commits. Implicit child drops do NOT fire their own events.</summary>
    public event Action<TableDroppedEvent>? TableDropped;

    /// <summary>Raised after <c>CREATE INDEX</c> commits on a stable table.</summary>
    public event Action<IndexCreatedEvent>? IndexCreated;

    /// <summary>Raised after <c>DROP INDEX</c> commits on a stable table.</summary>
    public event Action<IndexDroppedEvent>? IndexDropped;

    /// <summary>Raised after <c>CREATE FUNCTION</c> commits.</summary>
    public event Action<FunctionCreatedEvent>? FunctionCreated;

    /// <summary>Raised after <c>CREATE OR REPLACE FUNCTION</c> commits over an existing descriptor.</summary>
    public event Action<FunctionAlteredEvent>? FunctionAltered;

    /// <summary>Raised after <c>DROP FUNCTION</c> commits.</summary>
    public event Action<FunctionDroppedEvent>? FunctionDropped;

    /// <summary>Raised after <c>CREATE PROCEDURE</c> commits.</summary>
    public event Action<ProcedureCreatedEvent>? ProcedureCreated;

    /// <summary>Raised after <c>CREATE OR REPLACE PROCEDURE</c> commits over an existing descriptor.</summary>
    public event Action<ProcedureAlteredEvent>? ProcedureAltered;

    /// <summary>Raised after <c>DROP PROCEDURE</c> commits.</summary>
    public event Action<ProcedureDroppedEvent>? ProcedureDropped;

    /// <summary>Raised after <c>CREATE MODEL</c> commits.</summary>
    public event Action<ModelCreatedEvent>? ModelCreated;

    /// <summary>Raised after <c>CREATE OR REPLACE MODEL</c> commits over an existing descriptor.</summary>
    public event Action<ModelAlteredEvent>? ModelAltered;

    /// <summary>Raised after <c>DROP MODEL</c> commits.</summary>
    public event Action<ModelDroppedEvent>? ModelDropped;

    /// <summary>Raised after <c>CREATE VIEW</c> commits.</summary>
    public event Action<ViewCreatedEvent>? ViewCreated;

    /// <summary>Raised after <c>CREATE OR REPLACE VIEW</c> commits over an existing descriptor.</summary>
    public event Action<ViewAlteredEvent>? ViewAltered;

    /// <summary>Raised after <c>DROP VIEW</c> commits.</summary>
    public event Action<ViewDroppedEvent>? ViewDropped;

    // Raise methods are internal so only the catalog / routine registrar
    // can publish. Overload resolution picks the right channel per event
    // type so call sites stay readable (`_events.Raise(new FunctionCreatedEvent(...))`).
    internal void Raise(SchemaCreatedEvent e) => SchemaCreated?.Invoke(e);
    internal void Raise(SchemaDroppedEvent e) => SchemaDropped?.Invoke(e);
    internal void Raise(TableCreatedEvent e) => TableCreated?.Invoke(e);
    internal void Raise(TableAlteredEvent e) => TableAltered?.Invoke(e);
    internal void Raise(TableDroppedEvent e) => TableDropped?.Invoke(e);
    internal void Raise(IndexCreatedEvent e) => IndexCreated?.Invoke(e);
    internal void Raise(IndexDroppedEvent e) => IndexDropped?.Invoke(e);
    internal void Raise(FunctionCreatedEvent e) => FunctionCreated?.Invoke(e);
    internal void Raise(FunctionAlteredEvent e) => FunctionAltered?.Invoke(e);
    internal void Raise(FunctionDroppedEvent e) => FunctionDropped?.Invoke(e);
    internal void Raise(ProcedureCreatedEvent e) => ProcedureCreated?.Invoke(e);
    internal void Raise(ProcedureAlteredEvent e) => ProcedureAltered?.Invoke(e);
    internal void Raise(ProcedureDroppedEvent e) => ProcedureDropped?.Invoke(e);
    internal void Raise(ModelCreatedEvent e) => ModelCreated?.Invoke(e);
    internal void Raise(ModelAlteredEvent e) => ModelAltered?.Invoke(e);
    internal void Raise(ModelDroppedEvent e) => ModelDropped?.Invoke(e);
    internal void Raise(ViewCreatedEvent e) => ViewCreated?.Invoke(e);
    internal void Raise(ViewAlteredEvent e) => ViewAltered?.Invoke(e);
    internal void Raise(ViewDroppedEvent e) => ViewDropped?.Invoke(e);
}
