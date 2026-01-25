using System.Diagnostics;
using DatumIngest.Web.Hosting;
using Photino.NET;

namespace DatumIngest.Client;

internal static class Program
{
    // Hardcoded in dev mode so Vite's proxy config has a stable target. If
    // these ever conflict on a dev machine, swap both here and in vite.config.ts.
    private const string DevKestrelUrl = "http://127.0.0.1:5050";
    private const string DevViteUrl = "http://localhost:5173";

    // WebView2 on Windows requires the host thread to be STA. Top-level statements
    // with `await` would resume on a thread-pool (MTA) thread after the first await,
    // which causes WebView2 to render nothing and accept no input. Classic Main +
    // WebHost.Start (sync-bridged internally) preserves STA through to Photino.
    [STAThread]
    public static void Main(string[] args)
    {
        var isDev = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        var catalogRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatumIngest");
        Directory.CreateDirectory(catalogRootPath);

        // In dev: pin Kestrel to a known port so Vite's proxy config can target
        // it. Vite serves the SPA with HMR; API/hub calls proxy back here.
        // In prod: ephemeral port, Kestrel serves the built bundle.
        var bootstrap = new WebHostBootstrap(
            args,
            isDev ? DevKestrelUrl : "http://127.0.0.1:0",
            new WebHostOptions { CatalogRootPath = catalogRootPath });
        var host = WebHost.Start(bootstrap);

        Console.WriteLine($"[Client] Kestrel at {host.Url}, dev={isDev}");

        // Spawn Vite ourselves in dev mode (Microsoft.AspNetCore.SpaProxy's
        // hosting startup only fires when Web is the entry assembly; Client is).
        Process? vite = isDev ? ViteDevServer.Start() : null;

        // Chromeless windows require explicit size *and* location at startup
        // (Photino's StartupParameters validator rejects UseOsDefaultLocation /
        // UseOsDefaultSize when Chromeless = true). SetSize/SetLocation don't
        // flip those flags on their own — the explicit SetUseOsDefault*(false)
        // calls do. Center() then refines positioning after window creation.
        //
        // In dev, we open on a branded splash and navigate to Vite when it
        // responds (SpaProxy launches Vite asynchronously; navigating before
        // it's reachable would surface WebView2's chrome-error page).
        var initialWindow = new PhotinoWindow()
            .SetTitle("DatumIngest")
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(false)
            .SetSize(new System.Drawing.Size(1280, 800))
            .SetLocation(new System.Drawing.Point(100, 100))
            .SetResizable(true)
            .Center()
            .SetChromeless(true)
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true);

        var window = isDev
            ? initialWindow.LoadRawString(Splash.Html)
            : initialWindow.Load(host.Url);

        // Wire JS↔C# IPC. Window controls today; future modules (dialogs,
        // file pickers, native menus, Lua applet bridges) register through
        // the same HostBridge surface. See HostBridge.cs.
        _ = new HostBridge(window);

        if (isDev)
        {
            _ = Task.Run(() => WaitForViteAndNavigateAsync(window));
        }

        window.WaitForClose();

        if (vite is not null) ViteDevServer.Stop(vite);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Polls Vite's URL until it responds (or times out), then tells the splash
    // page to navigate. The splash listens for splash:navigate:<url> and sets
    // window.location.href; that's a top-level navigation so cross-origin CORS
    // doesn't apply.
    private static async Task WaitForViteAndNavigateAsync(PhotinoWindow window)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(DevViteUrl);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Client] Vite ready at {DevViteUrl} — navigating splash");
                    window.SendWebMessage(Splash.NavigateKind + DevViteUrl);
                    return;
                }
            }
            catch
            {
                // Not ready; HttpClient throws on connection-refused.
            }
            await Task.Delay(250);
        }

        Console.Error.WriteLine($"[Client] Vite did not respond at {DevViteUrl} within 60s");
        window.SendWebMessage("splash:error:Vite did not start within 60s. Check 'npm run dev' output.");
    }
}
