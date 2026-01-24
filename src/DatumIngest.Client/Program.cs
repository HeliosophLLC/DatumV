using DatumIngest.Web.Hosting;
using Photino.NET;

namespace DatumIngest.Client;

internal static class Program
{
    // WebView2 on Windows requires the host thread to be STA. Top-level statements
    // with `await` would resume on a thread-pool (MTA) thread after the first await,
    // which causes WebView2 to render nothing and accept no input. Classic Main +
    // WebHost.Start (sync-bridged internally) preserves STA through to Photino.
    [STAThread]
    public static void Main(string[] args)
    {
        var catalogRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatumIngest");
        Directory.CreateDirectory(catalogRootPath);

        var bootstrap = new WebHostBootstrap(
            args,
            "http://127.0.0.1:0",
            new WebHostOptions { CatalogRootPath = catalogRootPath });
        var host = WebHost.Start(bootstrap);

        Console.WriteLine($"[Client] Kestrel listening at {host.Url}");
        Console.WriteLine($"[Client] Opening Photino window…");

        var window = new PhotinoWindow()
            .SetTitle("DatumIngest")
            .SetSize(new System.Drawing.Size(1280, 800))
            .Center()
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true)
            .Load(host.Url);

        window.WaitForClose();

        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
