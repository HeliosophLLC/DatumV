using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods clients invoke on the server. Today the catalog hub is a
// one-way push channel — server announces DDL commits, clients listen.
// Ping kept so the typed-codegen pipeline has at least one method to
// emit, and so a client can sanity-check the connection.
[Hub]
public interface ICatalogHub
{
    Task Ping(string message);
}
