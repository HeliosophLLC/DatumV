using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Heliosoph.DatumV.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heliosoph.DatumV.Web;

public static class Program
{
    // Headless backend only — the Electron shell spawns this process as a
    // child and loads its renderer from Kestrel (prod) or Vite (dev with
    // proxy to Kestrel). See src/Heliosoph.DatumV.Web/electron/main.ts.
    public static void Main(string[] args)
    {
        EnsureAppLocalDllSearch();

        var url = Environment.GetEnvironmentVariable("DATUMV_WEB_URL") ?? "http://127.0.0.1:5000";
        var bootstrap = new WebHostBootstrap(
            args,
            url,
            new WebHostOptions
            {
                CatalogRootPath = ResolveCatalogRoot(),
                GlobalDataPath = ResolveGlobalDataPath(),
            });

        var host = WebHost.Start(bootstrap);
        // Electron's main process greps stdout for this line to detect ready.
        Console.WriteLine($"Heliosoph.DatumV listening at {host.Url}");

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
            services.AddDatumVWeb(new WebHostOptions { ManageLocalCatalog = false });
        }

        public void Configure(IApplicationBuilder app)
        {
            // No-op; NSwag only needs the DI graph for IDocumentProvider,
            // not the request pipeline.
        }
    }

    // Pins Windows's native-library search to AppContext.BaseDirectory so
    // app-local copies of vcruntime140.dll / msvcp140.dll / vulkan-1.dll
    // (shipped via Heliosoph.DatumV.Web.csproj) are found when LLamaSharp's
    // native binaries resolve their dependent imports.
    //
    // Why this is necessary: LLamaSharp finds llama.dll via explicit paths
    // built from AppContext.BaseDirectory, so the library file itself loads
    // fine. But Windows then resolves llama.dll's transitive imports using
    // its own LoadLibrary search rules, which for a subprocess launched by
    // Electron may not include AppContext.BaseDirectory by default. Calling
    // SetDllDirectory once at startup makes BaseDirectory the FIRST search
    // location for every subsequent native load — including the loader's
    // implicit dependency resolution.
    //
    // No-op on Linux + macOS (the API doesn't exist; those platforms use
    // libc / ld.so search semantics where the .so's own directory and
    // standard system paths are searched by default — no extra pin needed).
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static void EnsureAppLocalDllSearch()
    {
        if (!OperatingSystem.IsWindows()) return;
        bool ok = SetDllDirectory(AppContext.BaseDirectory);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine(
                $"[dll-search] SetDllDirectory({AppContext.BaseDirectory}) failed with Win32 error {err}. " +
                "Native libraries with dependencies on app-local VC++ runtime may fail to load.");
        }
    }

    // DATUMV_CATALOG_PATH is mandatory — the Electron shell sets it per
    // launch from the user's recent-catalogs list (or first-run prompt).
    // Failing fast here keeps every "is this catalog open?" code path
    // honest; nothing should ever guess a default.
    private static string ResolveCatalogRoot()
    {
        var path = Environment.GetEnvironmentVariable("DATUMV_CATALOG_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "DATUMV_CATALOG_PATH is required. The Electron shell sets this when " +
                "launching the backend. If running the backend directly, point it at " +
                "a workspace folder (one containing or that will contain .datum-catalog.json).");
        }
        Directory.CreateDirectory(path);
        return path;
    }

    // DATUMV_GLOBAL_PATH is optional but normally supplied by Electron so
    // both sides agree on the exact folder. When unset (e.g. running the
    // backend standalone in tests / scripts), fall back to the platform
    // user-data directory.
    private static string ResolveGlobalDataPath()
    {
        var path = Environment.GetEnvironmentVariable("DATUMV_GLOBAL_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heliosoph.DatumV");
        }
        Directory.CreateDirectory(path);
        return path;
    }
}
