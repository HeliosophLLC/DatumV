using DatumIngest.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DatumIngest.Web;

public static class Program
{
    // Real standalone host. Same seam DatumIngest.Client uses, with a fixed
    // URL and SIGINT handling. CreateHostBuilder (below) is what NSwag's
    // build-time scanner walks via assembly.EntryPoint.DeclaringType.
    public static int Main(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("DATUM_WEB_URL") ?? "http://127.0.0.1:5000";
        var bootstrap = new WebHostBootstrap(args, url, new WebHostOptions());

        var host = WebHost.Start(bootstrap);
        Console.WriteLine($"DatumIngest.Web listening at {host.Url}");

        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.StopApplication(); };
        lifetime.ApplicationStopping.WaitHandle.WaitOne();

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(builder => builder.UseStartup<NSwagDocumentStartup>());
}

internal sealed class NSwagDocumentStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDatumIngestWeb(new WebHostOptions());
    }

    public void Configure(IApplicationBuilder app)
    {
        // No-op; NSwag only needs the DI graph for IDocumentProvider, not the request pipeline.
    }
}
