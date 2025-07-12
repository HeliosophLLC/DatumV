using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DatumIngest.Editor;

/// <summary>
/// Extension methods for registering the DatumIngest SQL language server
/// SignalR hub in an ASP.NET host application.
/// </summary>
public static class EditorServiceExtensions
{
    /// <summary>
    /// Default endpoint path for the language server hub.
    /// </summary>
    internal const string DefaultHubPath = "/language-server";

    /// <summary>
    /// Maps the DatumIngest language server SignalR hub at the specified path.
    /// The host application must have already called
    /// <c>builder.Services.AddSignalR()</c> in its service configuration.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">
    /// The URL path to map the hub to. Defaults to <c>/language-server</c>.
    /// </param>
    /// <returns>The hub endpoint convention builder for further configuration.</returns>
    public static HubEndpointConventionBuilder MapDatumIngestEditor(
        this IEndpointRouteBuilder endpoints,
        string path = DefaultHubPath)
    {
        return endpoints.MapHub<LanguageServerHub>(path);
    }
}
