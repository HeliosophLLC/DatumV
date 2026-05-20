using BenchmarkDotNet.Running;

namespace Heliosoph.DatumV.Benchmarks;

/// <summary>
/// Entry point. Defaults to BenchmarkDotNet; <c>profile [select-all|select-where]
/// [seconds]</c> drops into a tight-loop driver that <c>dotnet-trace</c> can
/// attach to for CPU sampling without going through BDN's profiler validators.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "profile")
        {
            string query = args.Length > 1 ? args[1] : "select-all";
            int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 15;
            await ProfileDriver.RunAsync(query, TimeSpan.FromSeconds(seconds));
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
