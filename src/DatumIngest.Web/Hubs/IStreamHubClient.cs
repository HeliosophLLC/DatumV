using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods the server invokes on connected clients.
[Receiver]
public interface IStreamHubClient
{
    Task OnPong(string message);
}
