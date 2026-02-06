using System.Diagnostics;
using DatumIngest.Web.Hosting;
using DatumIngest.Web.Photino;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Photino.NET;

namespace DatumIngest.Web;

public static class Program
{
    // Pinned in dev mode so Vite's proxy config has a stable target. If this
    // ever conflicts on a dev machine, swap here and in vite.config.ts.
    // Vite's URL (http://localhost:5173) is hardcoded in Splash.cs's JS.
    private const string DevKestrelUrl = "http://127.0.0.1:5050";

    // WebView2 on Windows requires the host thread to be STA. Top-level
    // statements with `await` would resume on a thread-pool (MTA) thread
    // after the first await, which causes WebView2 to render nothing and
    // accept no input. Classic Main + WebHost.Start (sync-bridged internally)
    // preserves STA through to Photino. Harmless for the headless path.
    [STAThread]
    public static void Main(string[] args)
    {
        bool headless =
            args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Environment.GetEnvironmentVariable("DATUM_HEADLESS"), "1");

        if (headless)
        {
            RunHeadless(args);
        }
        else
        {
            RunDesktop(args);
        }
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

    private static void RunHeadless(string[] args)
    {
        var url = Environment.GetEnvironmentVariable("DATUM_WEB_URL") ?? "http://127.0.0.1:5000";
        var bootstrap = new WebHostBootstrap(
            args,
            url,
            new WebHostOptions { CatalogRootPath = ResolveCatalogRoot() });

        var host = WebHost.Start(bootstrap);
        Console.WriteLine($"DatumIngest listening at {host.Url}");

        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.StopApplication(); };
        lifetime.ApplicationStopping.WaitHandle.WaitOne();

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void RunDesktop(string[] args)
    {
        var isDev = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        // In dev: pin Kestrel to a known port so Vite's proxy config can
        // target it. Vite serves the SPA with HMR; API/hub calls proxy back
        // here. In prod: ephemeral port, Kestrel serves the built bundle.
        var bootstrap = new WebHostBootstrap(
            args,
            isDev ? DevKestrelUrl : "http://127.0.0.1:0",
            new WebHostOptions { CatalogRootPath = ResolveCatalogRoot() });
        var host = WebHost.Start(bootstrap);

        Console.WriteLine($"[Desktop] Kestrel at {host.Url}, dev={isDev}");

        // Spawn Vite ourselves in dev mode (Microsoft.AspNetCore.SpaProxy's
        // hosting startup doesn't fit this layout, and owning the launch
        // gives us explicit lifecycle).
        Process? vite = isDev ? ViteDevServer.Start() : null;

        // Chromeless + custom React title bar is Windows-only today. Mac/Linux
        // keep OS chrome until those platforms get the native drag/resize
        // integrations they need (NSWindow.performDrag on Mac, X11/Wayland
        // move-resize protocols on Linux). The chromeless-only validator also
        // requires explicit size *and* location at startup, so SetUseOsDefault*
        // (false) + explicit values gate together on the Windows branch.
        //
        // In dev, we open on a branded splash and navigate to Vite when it
        // responds; navigating before Vite is reachable would surface
        // WebView2's chrome-error page.
        var initialWindow = new PhotinoWindow()
            .SetTitle("DatumIngest")
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true);

        if (OperatingSystem.IsWindows())
        {
            initialWindow = initialWindow
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(new System.Drawing.Size(1280, 800))
                .SetLocation(new System.Drawing.Point(100, 100))
                .SetResizable(true)
                .Center()
                .SetChromeless(true);
        }

        var window = isDev
            ? initialWindow.LoadRawString(Splash.Html)
            : initialWindow.Load(host.Url);

        // Defer HostBridge construction (RegisterWebMessageReceivedHandler +
        // window event subscriptions) until after the native window actually
        // exists. The bridge reference is kept on a local so GC can't unwire
        // its event subscriptions.
        //
        // We do NOT send C# → JS messages during startup — calling
        // SendWebMessage before WebView2's inner IPC channel is wired up
        // produced an access-violation crash in Photino's native layer.
        // Splash polls Vite itself (see Splash.cs) and navigates when ready.
        HostBridge? bridge = null;
        window.WindowCreated += (_, _) =>
        {
            bridge = new HostBridge(window);
        };

        try
        {
            window.WaitForClose();
        }
        finally
        {
            // Always run cleanup, even if WaitForClose throws — otherwise the
            // Vite child process leaks and the next launch can't bind :5173.
            GC.KeepAlive(bridge);
            if (vite is not null) ViteDevServer.Stop(vite);
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
