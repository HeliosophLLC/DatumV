using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.Hubs;

/// <summary>
/// SignalR hub for catalog change notifications. Today this is a one-way
/// push channel — <see cref="CatalogEventBroadcastService"/> subscribes
/// to the in-process <see cref="DatumIngest.Catalog.CatalogEvents"/> bus
/// and forwards each commit to all connected clients via
/// <see cref="ICatalogHubClient.OnCatalogChanged"/>.
/// </summary>
/// <remarks>
/// Separate hub (not piggybacked on <see cref="StreamHub"/>) because the
/// event surface and lifecycle are unrelated: chat is per-connection and
/// stateful, catalog push is broadcast and stateless. Splitting also
/// keeps the receiver interfaces narrow.
/// </remarks>
public sealed class CatalogHub : Hub<ICatalogHubClient>, ICatalogHub
{
    public Task Ping(string message) =>
        Clients.Caller.OnPong($"pong: {message}");
}
