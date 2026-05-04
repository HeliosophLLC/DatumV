using DatumIngest.Catalog;
using DatumIngest.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.Catalog;

/// <summary>
/// Background service that bridges the in-process
/// <see cref="CatalogEvents"/> bus to SignalR. Subscribes once at startup
/// to all sixteen typed channels and forwards each commit to every
/// connected <see cref="CatalogHub"/> client as a
/// <see cref="CatalogChangedEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fire-and-forget.</strong> <see cref="CatalogEvents"/> handlers
/// run on the DDL caller's thread and must stay fast — see the bus's
/// remarks. We dispatch the SignalR send without awaiting it; the hub
/// context queues the message and returns immediately, but to be safe we
/// also wrap in a non-awaited task and swallow any push failure (a torn
/// transport shouldn't bubble out of a DDL statement).
/// </para>
/// <para>
/// <strong>Lifetime.</strong> Subscriptions are attached in
/// <see cref="StartAsync"/> and detached in <see cref="StopAsync"/>. The
/// TableCatalog itself outlives the service (singleton), so unsubscribing
/// on shutdown matters — otherwise a stopped service would still hold a
/// reference and try to push over a torn hub context.
/// </para>
/// </remarks>
internal sealed class CatalogEventBroadcastService : IHostedService
{
    private readonly TableCatalog _catalog;
    private readonly IHubContext<CatalogHub, ICatalogHubClient> _signalR;
    private readonly ILogger<CatalogEventBroadcastService> _logger;

    // Captured delegates so StopAsync can detach exactly the same instances
    // that StartAsync attached. C# events compare by delegate identity; a
    // fresh lambda on each call would silently fail to unsubscribe.
    private Action<SchemaCreatedEvent>? _onSchemaCreated;
    private Action<SchemaDroppedEvent>? _onSchemaDropped;
    private Action<TableCreatedEvent>? _onTableCreated;
    private Action<TableAlteredEvent>? _onTableAltered;
    private Action<TableDroppedEvent>? _onTableDropped;
    private Action<IndexCreatedEvent>? _onIndexCreated;
    private Action<IndexDroppedEvent>? _onIndexDropped;
    private Action<FunctionCreatedEvent>? _onFunctionCreated;
    private Action<FunctionAlteredEvent>? _onFunctionAltered;
    private Action<FunctionDroppedEvent>? _onFunctionDropped;
    private Action<ProcedureCreatedEvent>? _onProcedureCreated;
    private Action<ProcedureAlteredEvent>? _onProcedureAltered;
    private Action<ProcedureDroppedEvent>? _onProcedureDropped;
    private Action<ModelCreatedEvent>? _onModelCreated;
    private Action<ModelAlteredEvent>? _onModelAltered;
    private Action<ModelDroppedEvent>? _onModelDropped;
    private Action<ViewCreatedEvent>? _onViewCreated;
    private Action<ViewAlteredEvent>? _onViewAltered;
    private Action<ViewDroppedEvent>? _onViewDropped;

    public CatalogEventBroadcastService(
        TableCatalog catalog,
        IHubContext<CatalogHub, ICatalogHubClient> signalR,
        ILogger<CatalogEventBroadcastService> logger)
    {
        _catalog = catalog;
        _signalR = signalR;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        CatalogEvents events = _catalog.Events;

        _onSchemaCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.SchemaCreated, Schema: null, Name: e.SchemaName, ChildName: null));
        _onSchemaDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.SchemaDropped, Schema: null, Name: e.SchemaName, ChildName: null));

        _onTableCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.TableCreated, e.Name.Schema, e.Name.Name, ChildName: null));
        _onTableAltered = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.TableAltered, e.Name.Schema, e.Name.Name, ChildName: null));
        _onTableDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.TableDropped, e.Name.Schema, e.Name.Name, ChildName: null));

        _onIndexCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.IndexCreated, e.TableName.Schema, e.TableName.Name, ChildName: e.After.Name));
        _onIndexDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.IndexDropped, e.TableName.Schema, e.TableName.Name, ChildName: e.Before?.Name));

        _onFunctionCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.FunctionCreated, e.Name.Schema, e.Name.Name, ChildName: null));
        _onFunctionAltered = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.FunctionAltered, e.Name.Schema, e.Name.Name, ChildName: null));
        _onFunctionDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.FunctionDropped, e.Name.Schema, e.Name.Name, ChildName: null));

        _onProcedureCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ProcedureCreated, e.Name.Schema, e.Name.Name, ChildName: null));
        _onProcedureAltered = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ProcedureAltered, e.Name.Schema, e.Name.Name, ChildName: null));
        _onProcedureDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ProcedureDropped, e.Name.Schema, e.Name.Name, ChildName: null));

        _onModelCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ModelCreated, e.Name.Schema, e.Name.Name, ChildName: null));
        _onModelAltered = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ModelAltered, e.Name.Schema, e.Name.Name, ChildName: null));
        _onModelDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ModelDropped, e.Name.Schema, e.Name.Name, ChildName: null));

        _onViewCreated = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ViewCreated, e.Name.Schema, e.Name.Name, ChildName: null));
        _onViewAltered = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ViewAltered, e.Name.Schema, e.Name.Name, ChildName: null));
        _onViewDropped = e => Broadcast(new CatalogChangedEvent(
            CatalogChangeKind.ViewDropped, e.Name.Schema, e.Name.Name, ChildName: null));

        events.SchemaCreated += _onSchemaCreated;
        events.SchemaDropped += _onSchemaDropped;
        events.TableCreated += _onTableCreated;
        events.TableAltered += _onTableAltered;
        events.TableDropped += _onTableDropped;
        events.IndexCreated += _onIndexCreated;
        events.IndexDropped += _onIndexDropped;
        events.FunctionCreated += _onFunctionCreated;
        events.FunctionAltered += _onFunctionAltered;
        events.FunctionDropped += _onFunctionDropped;
        events.ProcedureCreated += _onProcedureCreated;
        events.ProcedureAltered += _onProcedureAltered;
        events.ProcedureDropped += _onProcedureDropped;
        events.ModelCreated += _onModelCreated;
        events.ModelAltered += _onModelAltered;
        events.ModelDropped += _onModelDropped;
        events.ViewCreated += _onViewCreated;
        events.ViewAltered += _onViewAltered;
        events.ViewDropped += _onViewDropped;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        CatalogEvents events = _catalog.Events;

        if (_onSchemaCreated is not null) events.SchemaCreated -= _onSchemaCreated;
        if (_onSchemaDropped is not null) events.SchemaDropped -= _onSchemaDropped;
        if (_onTableCreated is not null) events.TableCreated -= _onTableCreated;
        if (_onTableAltered is not null) events.TableAltered -= _onTableAltered;
        if (_onTableDropped is not null) events.TableDropped -= _onTableDropped;
        if (_onIndexCreated is not null) events.IndexCreated -= _onIndexCreated;
        if (_onIndexDropped is not null) events.IndexDropped -= _onIndexDropped;
        if (_onFunctionCreated is not null) events.FunctionCreated -= _onFunctionCreated;
        if (_onFunctionAltered is not null) events.FunctionAltered -= _onFunctionAltered;
        if (_onFunctionDropped is not null) events.FunctionDropped -= _onFunctionDropped;
        if (_onProcedureCreated is not null) events.ProcedureCreated -= _onProcedureCreated;
        if (_onProcedureAltered is not null) events.ProcedureAltered -= _onProcedureAltered;
        if (_onProcedureDropped is not null) events.ProcedureDropped -= _onProcedureDropped;
        if (_onModelCreated is not null) events.ModelCreated -= _onModelCreated;
        if (_onModelAltered is not null) events.ModelAltered -= _onModelAltered;
        if (_onModelDropped is not null) events.ModelDropped -= _onModelDropped;
        if (_onViewCreated is not null) events.ViewCreated -= _onViewCreated;
        if (_onViewAltered is not null) events.ViewAltered -= _onViewAltered;
        if (_onViewDropped is not null) events.ViewDropped -= _onViewDropped;

        return Task.CompletedTask;
    }

    private void Broadcast(CatalogChangedEvent change)
    {
        // The hub context's SendAsync queues to an internal channel and
        // returns a Task that completes when delivery has been *attempted*.
        // Awaiting on the DDL thread would couple statement latency to
        // SignalR transport health; fire-and-forget with a logged catch
        // is the safer choice here.
        _ = Task.Run(async () =>
        {
            try
            {
                await _signalR.Clients.All.OnCatalogChanged(change).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast catalog change {Kind} {Schema}.{Name}",
                    change.Kind, change.Schema, change.Name);
            }
        });
    }
}
