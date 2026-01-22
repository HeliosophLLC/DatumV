using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods clients invoke on the server.
[Hub]
public interface IStreamHub
{
    Task Ping(string message);
}
