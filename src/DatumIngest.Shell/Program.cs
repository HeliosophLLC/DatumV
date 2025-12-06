using DatumIngest.Catalog;
using DatumIngest.Models;
using DatumIngest.Shell;
using Spectre.Console;

// datum-shell [--models <path>] <path>...
//
//   Opens an interactive SQL REPL over one or more .datum files or directories.
//   Each path may be a single .datum file or a directory of .datum files.
//   Inside the REPL, type SQL terminated by `;` to execute; type `EXPLAIN <sql>;`
//   or `EXPLAIN ANALYZE <sql>;` to inspect the plan; `.help`, `.quit`, `.exit`.
//
//   Model directory resolution order:
//     --models <path> CLI flag  →  DATUM_MODELS env var  →  per-user default
//     (%LOCALAPPDATA%/DatumIngest/models on Windows, ~/.local/share/... on Linux).

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

string? modelsOverride = null;
List<string> dataPaths = new();
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg == "--models")
    {
        if (++i >= args.Length)
        {
            AnsiConsole.MarkupLine("[red]--models requires a path.[/]");
            return 1;
        }
        modelsOverride = args[i];
    }
    else
    {
        dataPaths.Add(arg);
    }
}

if (dataPaths.Count == 0)
{
    AnsiConsole.MarkupLine("[red]No data paths provided.[/]");
    PrintUsage();
    return 1;
}

DatumIngest.Pooling.Pool pool = new(new DatumIngest.Pooling.PoolBacking());
TableCatalog catalog = new(pool);

// One-call setup: ModelCatalog rooted at the resolved models directory,
// every builtin model registered (vision, captioner, image-gen, LLM,
// STT, TTS), plus the system_models virtual table for
// `SELECT * FROM system_models` introspection. Models directory precedence:
// --models flag → DATUM_MODELS env var → per-user default. Models load
// lazily; missing files surface at plan time with a clear redownload hint.
// Python-bridge models (Kokoro, Bark) are wired by AttachStandardModels
// too -- they show status=bridge in system_models until their venvs and
// worker scripts are set up.
ModelCatalog modelCatalog = BuiltinModels.AttachStandardModels(catalog, modelsOverride);

// Kokoro-82M voice override: the bundled voices-v1.0.bin default works
// out of the box, but if you have per-voice .bin files instead, point at
// the directory here and reset the default voice to one you actually have.
modelCatalog.Unregister("kokoro_82m");
BuiltinModels.RegisterKokoro82M(
    modelCatalog,
    voicesPath: "kokoro-voices",
    defaultVoice: "af_bella");

int datumFilesAdded = 0;
try
{
    foreach (string path in dataPaths)
    {
        if (Directory.Exists(path))
        {
            foreach (string file in Directory.EnumerateFiles(path, "*.datum", SearchOption.TopDirectoryOnly))
            {
                catalog.AddFile(file);
                datumFilesAdded++;
            }
        }
        else if (File.Exists(path))
        {
            catalog.AddFile(path);
            datumFilesAdded++;
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

// Track datum-file count explicitly because `catalog.Count` includes the
// always-present `system_models` virtual table — using it as the empty-data
// guard would silently let a no-args invocation slip through.
if (datumFilesAdded == 0)
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
    Console.Error.WriteLine("Usage: datum-shell [--models <path>] <path>...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Each <path> is either a .datum file or a directory of .datum files.");
    Console.Error.WriteLine("  --models <path>   Override the model files directory.");
    Console.Error.WriteLine("                    Falls back to DATUM_MODELS env var, then a per-user default.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Inside the REPL: SQL terminated by `;`, `EXPLAIN [ANALYZE] <sql>;`,");
    Console.Error.WriteLine("                   `.help`, `.quit`, `.exit`. Ctrl+C cancels a running query.");
    Console.Error.WriteLine("                   `SELECT * FROM system.models` lists registered models.");
}

