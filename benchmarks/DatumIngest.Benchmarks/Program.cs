using BenchmarkDotNet.Running;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Entry point for BenchmarkDotNet runner.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
