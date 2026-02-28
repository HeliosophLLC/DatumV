using TypedSignalR.Client;

namespace DatumIngest.Web.Hubs;

// Methods the server invokes on connected clients. Single fan-out method
// carrying a discriminated CatalogChangedEvent — see the DTO file for the
// rationale on lean-vs-thick payloads.
[Receiver]
public interface ICatalogHubClient
{
    Task OnPong(string message);

    Task OnCatalogChanged(CatalogChangedEvent change);
}
