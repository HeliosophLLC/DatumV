using System.Diagnostics;
using DatumIngest.Web.Hosting;
using Photino.NET;

namespace DatumIngest.Client;

internal static class Program
{
    // Hardcoded in dev mode so Vite's proxy config has a stable target. If
    // this ever conflicts on a dev machine, swap here and in vite.config.ts.
    // Vite's URL (http://localhost:5173) is hardcoded in Splash.cs's JS.
    private const string DevKestrelUrl = "http://127.0.0.1:5050";

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

        // Chromeless + custom React title bar is Windows-only today. Mac/Linux
        // keep OS chrome until those platforms get the native drag/resize
        // integrations they need (NSWindow.performDrag on Mac, X11/Wayland
        // move-resize protocols on Linux). The chromeless-only validator also
        // requires explicit size *and* location at startup, so SetUseOsDefault*
        // (false) + explicit values gate together on the Windows branch.
        //
        // In dev, we open on a branded splash and navigate to Vite when it
        // responds; navigating before Vite is reachable would surface WebView2's
        // chrome-error page.
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
        // exists. The bridge reference is kept on Program (not discarded) so
        // GC can't unwire its event subscriptions.
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
}
