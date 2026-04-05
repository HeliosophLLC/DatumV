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
long? vramBudgetOverrideBytes = null;
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
    else if (arg == "--vram-budget-gb")
    {
        if (++i >= args.Length)
        {
            AnsiConsole.MarkupLine("[red]--vram-budget-gb requires a number (e.g. 18 or 18.5).[/]");
            return 1;
        }
        if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double gb) || gb <= 0)
        {
            AnsiConsole.MarkupLine($"[red]Invalid --vram-budget-gb value: {Markup.Escape(args[i])}[/]");
            return 1;
        }
        vramBudgetOverrideBytes = (long)(gb * 1024 * 1024 * 1024);
    }
    else if (arg == "--vram-budget-unlimited")
    {
        vramBudgetOverrideBytes = ModelResidencyManager.UnlimitedBudget;
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
ModelCatalog modelCatalog = BuiltinModels.AttachStandardModels(
    catalog, modelsOverride, vramBudgetBytes: vramBudgetOverrideBytes);

// Flush calibration on Ctrl+C and on normal process exit. InteractiveShell's
// Ctrl+C handler only cancels an active query; with no query running, the
// default behavior kills the process before `using (catalog)` would
// otherwise call Dispose. A measured curve is ~tens of seconds of work
// per model — losing it because Dispose didn't run is the durability gap
// users actually hit. Both handlers route through SaveCalibrationNow,
// which is idempotent (re-serialises the same in-memory registry).
Console.CancelKeyPress += (_, _) => modelCatalog.SaveCalibrationNow();
AppDomain.CurrentDomain.ProcessExit += (_, _) => modelCatalog.SaveCalibrationNow();

// Show the resolved budget at startup so users can see what auto-detection
// picked. The residency manager already logs per-load lines; this is the
// "where did the budget come from" header.
if (modelCatalog.VramBudgetBytes == ModelResidencyManager.UnlimitedBudget)
{
    AnsiConsole.MarkupLine("[grey]VRAM budget: [yellow]unlimited[/] (no eviction; risks shared-RAM spillover)[/]");
}
else
{
    double gb = modelCatalog.VramBudgetBytes / (1024.0 * 1024.0 * 1024.0);
    AnsiConsole.MarkupLine($"[grey]VRAM budget: [white]{gb:F1} GB[/] (residency manager evicts LRU when exceeded)[/]");
}

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
    Console.Error.WriteLine("  --vram-budget-gb <n>     Override the auto-detected VRAM budget (e.g. 18 or 18.5).");
    Console.Error.WriteLine("  --vram-budget-unlimited  Disable residency eviction (risks shared-RAM spillover).");
    Console.Error.WriteLine("                           Auto-detection queries nvidia-smi and subtracts a 4 GB headroom.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Inside the REPL: SQL terminated by `;`, `EXPLAIN [ANALYZE] <sql>;`,");
    Console.Error.WriteLine("                   `.help`, `.quit`, `.exit`. Ctrl+C cancels a running query.");
    Console.Error.WriteLine("                   `SELECT * FROM system.models` lists registered models.");
}

