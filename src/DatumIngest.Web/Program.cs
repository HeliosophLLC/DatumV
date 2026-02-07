using DatumIngest.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DatumIngest.Web;

public static class Program
{
    // Headless backend only — the Electron shell spawns this process as a
    // child and loads its renderer from Kestrel (prod) or Vite (dev with
    // proxy to Kestrel). See src/DatumIngest.Web/electron/main.ts.
    public static void Main(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("DATUM_WEB_URL") ?? "http://127.0.0.1:5000";
        var bootstrap = new WebHostBootstrap(
            args,
            url,
            new WebHostOptions { CatalogRootPath = ResolveCatalogRoot() });

        var host = WebHost.Start(bootstrap);
        // Electron's main process greps stdout for this line to detect ready.
        Console.WriteLine($"DatumIngest listening at {host.Url}");

        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.StopApplication(); };
        lifetime.ApplicationStopping.WaitHandle.WaitOne();

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // NSwag's build-time scanner reflects on this method via
    // assembly.EntryPoint.DeclaringType to walk the DI graph for
    // IDocumentProvider. ManageLocalCatalog=false in the inner startup
    // skips every service that does real I/O or network on registration
    // (catalog open, migrations, model attach, LLM load, model-catalog
    // initialisation) — just enough wiring for NSwag to enumerate
    // controllers + DTOs.
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(builder => builder.UseStartup<NSwagDocumentStartup>());

    private sealed class NSwagDocumentStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDatumIngestWeb(new WebHostOptions { ManageLocalCatalog = false });
        }

        public void Configure(IApplicationBuilder app)
        {
            // No-op; NSwag only needs the DI graph for IDocumentProvider,
            // not the request pipeline.
        }
    }

    private static string ResolveCatalogRoot()
    {
        var path = Environment.GetEnvironmentVariable("DATUM_CATALOG_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DatumIngest");
        Directory.CreateDirectory(path);
        return path;
    }
}
