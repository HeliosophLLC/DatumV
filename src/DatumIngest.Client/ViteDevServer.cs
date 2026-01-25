using System.Diagnostics;

namespace DatumIngest.Client;

// Spawns the Vite dev server as a child process in dev mode and kills it on
// shutdown. Standard ASP.NET SpaProxy doesn't fire when our entry assembly
// isn't the Web project (Client is the entry); rather than fight the
// hosting-startup discovery, we own the launch directly.
internal static class ViteDevServer
{
    public static Process Start()
    {
        var clientAppPath = FindClientAppPath();

        // On Windows, invoke through cmd.exe /c rather than calling npm.cmd
        // directly. With UseShellExecute=false, .NET loads the .cmd as if it
        // were a PE binary instead of routing through cmd.exe — npm.cmd's
        // `%~dp0` then resolves to the working directory, sending npm to
        // look for npm-cli.js inside the project's node_modules. cmd.exe /c
        // makes it behave like a normal shell invocation.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c npm run dev",
                WorkingDirectory = clientAppPath,
            }
            : new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "run dev",
                WorkingDirectory = clientAppPath,
            };

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        // Explicit redirect — when Client runs under VS Code's
        // integratedTerminal, stdout inheritance through Process.Start
        // can drop the child's output. Forwarding ourselves guarantees we
        // see Vite logs and any startup errors.
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'npm run dev' in " + clientAppPath);

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[Vite] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine($"[Vite ERR] {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
            Console.Error.WriteLine($"[Vite] process exited with code {process.ExitCode}");

        Console.WriteLine($"[Vite] launched PID {process.Id} in {clientAppPath}");
        return process;
    }

    public static void Stop(Process process)
    {
        if (process.HasExited) return;
        try
        {
            Console.WriteLine($"[Vite] stopping PID {process.Id}");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Vite] failed to stop cleanly: {ex.Message}");
        }
    }

    // Walks up from AppContext.BaseDirectory (the Client's bin folder at
    // runtime) until it finds src/DatumIngest.Web/ClientApp/. Robust to
    // changes in launch cwd; doesn't rely on fragile relative paths.
    private static string FindClientAppPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DatumIngest.Web", "ClientApp");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate src/DatumIngest.Web/ClientApp/ walking up from {AppContext.BaseDirectory}");
    }
}
