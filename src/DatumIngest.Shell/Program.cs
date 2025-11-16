using DatumIngest.Catalog;
using DatumIngest.Models;
using DatumIngest.Shell;
using Spectre.Console;

// datum-shell <path>...
//
//   Opens an interactive SQL REPL over one or more .datum files or directories.
//   Each path may be a single .datum file or a directory of .datum files.
//   Inside the REPL, type SQL terminated by `;` to execute; type `EXPLAIN <sql>;`
//   or `EXPLAIN ANALYZE <sql>;` to inspect the plan; `.help`, `.quit`, `.exit`.

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

TableCatalog catalog = new(new DatumIngest.Pooling.Pool(new DatumIngest.Pooling.PoolBacking()));

// Register the built-in model catalog so `models.*` calls resolve. Each
// model loads lazily on first use, so missing files don't block startup —
// `models.foo(...)` calls fail at the moment they're actually evaluated.
// The residency manager runs with an unlimited budget by default; switch
// to the (modelDirectory, vramBudgetBytes, admissionTimeout) ctor when you
// want eviction to kick in (e.g. on a 12 GB card hosting a captioner +
// LLM + diffusion model concurrently).
ModelCatalog modelCatalog = new();
BuiltinModels.RegisterMobileNetV2(modelCatalog);
BuiltinModels.RegisterYolo(modelCatalog);
BuiltinModels.RegisterLlama31(modelCatalog);
BuiltinModels.RegisterPhi3(modelCatalog);
catalog.Models = modelCatalog;
try
{
    foreach (string path in args)
    {
        if (Directory.Exists(path))
        {
            foreach (string file in Directory.EnumerateFiles(path, "*.datum", SearchOption.TopDirectoryOnly))
            {
                catalog.AddFile(file);
            }
        }
        else if (File.Exists(path))
        {
            catalog.AddFile(path);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Path not found: {Markup.Escape(path)}[/]");
            catalog.Dispose();
            return 1;
        }
    }
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Failed to register tables: {Markup.Escape(ex.Message)}[/]");
    catalog.Dispose();
    return 1;
}

if (catalog.Count == 0)
{
    AnsiConsole.MarkupLine("[red]No .datum files registered.[/]");
    catalog.Dispose();
    return 1;
}

using (catalog)
{
    InteractiveShell shell = new(catalog);
    return await shell.RunAsync(CancellationToken.None);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: datum-shell <path>...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Each <path> is either a .datum file or a directory of .datum files.");
    Console.Error.WriteLine("  Inside the REPL: SQL terminated by `;`, `EXPLAIN [ANALYZE] <sql>;`,");
    Console.Error.WriteLine("                   `.help`, `.quit`, `.exit`. Ctrl+C cancels a running query.");
}
