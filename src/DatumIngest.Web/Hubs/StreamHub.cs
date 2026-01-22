using Microsoft.AspNetCore.SignalR;

namespace DatumIngest.Web.Hubs;

public sealed class StreamHub : Hub<IStreamHubClient>, IStreamHub
{
    public Task Ping(string message) =>
        Clients.Caller.OnPong($"pong: {message}");
}
