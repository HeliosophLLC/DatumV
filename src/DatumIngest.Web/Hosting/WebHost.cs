using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DatumIngest.Web.Hosting;

public static class WebHost
{
    // Builds and starts the WebApplication using bootstrap. Returns once Kestrel
    // is listening. Caller is responsible for disposing the StartedWebHost.
    //
    // Synchronous bridge inside is intentional and isolated to host boot:
    // DatumIngest.Client requires the calling thread to remain STA for
    // WebView2 (see project_datumingest_client_sta_requirement.md). Standalone
    // callers (Web/Program.cs) can ignore that, but the sync surface is shared.
    public static StartedWebHost Start(WebHostBootstrap bootstrap)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = bootstrap.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls(bootstrap.Url);
        builder.Services.AddDatumIngestWeb(bootstrap.Options);

        var app = builder.Build();
        app.UseDatumIngestWeb();

        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not bind to any address.");

        return new StartedWebHost(app, new Uri(address));
    }
}
