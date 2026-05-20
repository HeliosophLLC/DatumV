using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Benchmarks;

/// <summary>
/// Standalone profile driver that bypasses BenchmarkDotNet. Sets up a DatumFile
/// fixture once, then runs the chosen query in a tight loop for the requested
/// duration so an external profiler — <c>dotnet-trace</c>, dotTrace, VS Profiler
/// — can attach by process ID and capture CPU samples.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// # Terminal 1 — start the loop:
/// dotnet run -c Release --project benchmarks/Heliosoph.DatumV.Benchmarks --no-restore -- profile select-where 30
///
/// # Terminal 2 — attach dotnet-trace by name (BDN spawns the worker as dotnet.exe):
/// dotnet-trace collect --name Heliosoph.DatumV.Benchmarks --duration 00:00:25 \
///     --providers Microsoft-DotNETCore-SampleProfiler --output profile.nettrace
///
/// # Convert to speedscope JSON for browser viewing:
/// dotnet-trace convert profile.nettrace --format speedscope
/// </code>
/// First two seconds of execution are a JIT/cache warmup and excluded from the
/// reported iteration count.
/// </remarks>
internal static class ProfileDriver
{
    private static readonly ColumnDescriptorV2[] DataColumns =
    [
        new("id", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("name", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("value", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
        new("category", DataKind.String, EncoderKind.VariableSlot, IsNullable: false),
        new("score", DataKind.Float32, EncoderKind.FixedWidth, IsNullable: false),
    ];

    public static async Task RunAsync(string queryName, TimeSpan duration)
    {
        string sql = queryName switch
        {
            "select-all" => "SELECT * FROM data",
            "select-where" => "SELECT id, name, value FROM data WHERE value > 500",
            _ => throw new ArgumentException(
                $"Unknown profile query '{queryName}'. Use 'select-all' or 'select-where'.",
                nameof(queryName)),
        };

        Console.WriteLine($"[profile] query='{queryName}' sql=\"{sql}\" duration={duration.TotalSeconds:F0}s pid={Environment.ProcessId}");
        Console.WriteLine($"[profile] attach with: dotnet-trace collect --process-id {Environment.ProcessId} --duration 00:00:{Math.Max(1, (int)(duration.TotalSeconds - 2)):D2} --providers Microsoft-DotNETCore-SampleProfiler --output profile.nettrace");

        // Setup: write a .datum file, build the planned operator tree once. All
        // subsequent iterations only exercise execute, matching what we actually
        // want to profile.
        Pool pool = new(new PoolBacking());
        string tempDir = Path.Combine(Path.GetTempPath(), $"datum_profile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Row[] rows = SyntheticDataGenerator.GenerateRows(10_000);
            string dataPath = DatumFileBenchHelper.WriteRowsToDatumFile(
                pool, Path.Combine(tempDir, "data.datum"), rows, DataColumns);

            TableCatalog catalog = new(pool);
            catalog.Add(new TableDescriptor("data", dataPath));
            FunctionRegistry functions = FunctionRegistry.CreateDefault();
            QueryExpression query = SqlParser.Parse(sql);
            QueryPlanner planner = new(catalog, functions);
            QueryOperator root = planner.Plan(query);
            ExecutionContext context = catalog.CreateExecutionContext();

            // Warmup — give the JIT a chance to settle before the trace window opens.
            DateTime warmupEnd = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < warmupEnd)
            {
                await ExecuteOnce(root, context);
            }
            Console.WriteLine("[profile] warmup complete — attach the profiler now.");

            // Steady-state loop. Tight, no thinking time — keeps CPU busy so the
            // profiler's sample rate gets enough samples on the hot path.
            DateTime sampleEnd = DateTime.UtcNow.Add(duration);
            long iterations = 0;
            while (DateTime.UtcNow < sampleEnd)
            {
                await ExecuteOnce(root, context);
                iterations++;
            }

            Console.WriteLine(
                $"[profile] done — {iterations} iterations in {duration.TotalSeconds:F0}s "
                + $"= {iterations / duration.TotalSeconds:F0} iter/s "
                + $"({(duration.TotalMilliseconds / iterations):F3} ms/iter)");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task ExecuteOnce(QueryOperator root, ExecutionContext context)
    {
        await foreach (RowBatch batch in root.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }
    }
}
