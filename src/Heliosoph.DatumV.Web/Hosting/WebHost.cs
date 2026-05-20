using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Heliosoph.DatumV.Web.Hosting;

public static class WebHost
{
    /// <summary>
    /// Builds and starts the WebApplication using bootstrap. Returns once Kestrel
    /// is listening. Caller is responsible for disposing the StartedWebHost.
    /// </summary>
    /// <param name="bootstrap"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static StartedWebHost Start(WebHostBootstrap bootstrap)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = bootstrap.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls(bootstrap.Url);
        builder.Services.AddDatumVWeb(bootstrap.Options);
        builder.Services.AddFileFormats();

        var app = builder.Build();
        app.UseDatumVWeb();

        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not bind to any address.");

        return new StartedWebHost(app, new Uri(address));
    }
}
