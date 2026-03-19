using System.Diagnostics;
using System.Text;

using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Diagnostics;
using DatumIngest.Functions;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Pooling;

using Microsoft.Extensions.Logging.Abstractions;

// probe — debug harness for running ad-hoc SQL against the installed model catalog.
//
//   probe "<sql>"                          run sql against the current catalog
//   probe --sql-file <path>                ditto, read sql from file
//   probe --apply <path-to-create-model>   run a CREATE MODEL statement
//                                          BEFORE the main sql (transient — not
//                                          persisted to the catalog file).
//                                          Can be repeated for multiple bodies.
//   probe --table <name>=<path-to.datum>   mount a .datum file as a table.
//                                          Repeatable. Convenient short forms:
//                                            --coco                 mounts
//                                              E:\Datasets\COCO2017\test2017.datum
//                                              as `test2017` (matches the filename
//                                              so SQL can say FROM test2017).
//                                            --crimes               mounts
//                                              E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.datum
//                                              as `crimes`.
//
// Catalog resolution:
//   --catalog-root <path>     defaults to %LOCALAPPDATA%\DatumIngest
//   --models-dir   <path>     defaults to $DATUM_MODELS, then
//                              %LOCALAPPDATA%\DatumIngest\models
//   --no-builtins             skip BuiltinModels.AttachStandardModels (faster
//                             startup when you only need SQL-defined models)
//   --no-rehydrate            don't re-apply persisted CREATE MODEL statements
//                             from the catalog file
//
// Output:
//   --limit <N>               cap printed rows (default 100)
//   --all                     print every row
//
// Repetition:
//   --repeat <N>              run the SQL N times back-to-back in the same
//                             process (same catalog, same model residency).
//                             Exists to reproduce cross-query state corruption
//                             — e.g. when a model works on the first call but
//                             crashes on the second. Default 1.
//   --also <sql>              additional SQL statements to run after the
//                             primary SQL in the SAME process. Repeatable —
//                             useful for reproducing crashes that only manifest
//                             after a specific sequence of varying queries
//                             (e.g. LIMIT 1, LIMIT 1000, LIMIT 1, LIMIT 1).
//                             Each --also statement gets its own --repeat-count
//                             iteration too.
//
// Live execution diagnostics:
//   --activity-log <N>        Enable a RecentActivityLog with capacity N (default
//                             100 when the flag is passed without a value).
//                             Press Ctrl+C during a hung query to dump:
//                               1. Live operators — the live operator stack at
//                                  the moment of cancellation, sorted oldest
//                                  first (leaf = where execution is parked).
//                               2. Recent activity — the last N completed
//                                  spans (operator pulls + scalar function
//                                  dispatches) with parent linkage.
//                             Useful for "where is the engine right now?"
//   --activity-source <name>  Repeatable. Filter --activity-log output to
//                             only show spans from sources named <name>.
//                             Accepts the short forms "op" (operator pulls)
//                             and "fn" (scalar function calls), or the full
//                             source name "DatumIngest.Operators" /
//                             "DatumIngest.Scalars". Omit to see all.

Console.OutputEncoding = Encoding.UTF8;

Options opts;
try
{
    opts = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  probe \"<sql>\" [--catalog-root <path>] [--models-dir <path>] [--apply <create-model.sql>]");
    Console.Error.WriteLine("                 [--no-builtins] [--no-rehydrate] [--limit N | --all]");
    Console.Error.WriteLine("  probe --sql-file <path> [...same flags...]");
    return 1;
}

string primarySql = opts.SqlFile is not null
    ? await File.ReadAllTextAsync(opts.SqlFile)
    : opts.Sql!;

// Trim trailing semicolons + whitespace — pasting from a SQL client
// (DBeaver / VS Code SQL extensions) usually leaves them in, and
// single-statement PlanAsync rejects them as a syntax error.
static string TrimSql(string s) => s.TrimEnd().TrimEnd(';').TrimEnd();

List<string> sqls = [TrimSql(primarySql)];
foreach (string also in opts.AlsoSql)
{
    sqls.Add(TrimSql(also));
}

// Catalog root defaults to a fresh temp dir so the probe doesn't fight the
// running app over file locks (the catalog opens its tables exclusively).
// The models directory is shared because the .onnx files are read-only —
// no contention there.
string catalogRoot = opts.CatalogRoot
    ?? Path.Combine(Path.GetTempPath(), $"datum-probe-{Guid.NewGuid():N}");

string modelsDir = opts.ModelsDir
    ?? Environment.GetEnvironmentVariable("DATUM_MODELS")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DatumIngest", "models");

bool catalogRootIsScratch = opts.CatalogRoot is null;
Directory.CreateDirectory(catalogRoot);

string catalogFile = Path.Combine(catalogRoot, CatalogStore.DefaultFileName);

Console.WriteLine($"Catalog root:   {catalogRoot}{(catalogRootIsScratch ? "  (scratch, will be removed)" : "")}");
Console.WriteLine($"Models dir:     {modelsDir}{(Directory.Exists(modelsDir) ? "" : "  (does not exist)")}");
Console.WriteLine();

// Pool + dispatcher are the only DI-level dependencies; the catalog
// constructor handles the rest. Skipping the full ServiceCollection
// dance keeps the probe's startup ~instant.
PoolBacking backing = new();
Pool pool = new(backing);
InferenceDispatcher dispatcher = new(
    [new OnnxRuntimeBackend()],
    NullLogger<InferenceDispatcher>.Instance);

// Manual disposal (not `using`) so scratch-dir cleanup at the very end
// of Main runs AFTER the catalog releases file locks.
TableCatalog catalog = new(pool, catalogFile);
catalog.InferenceDispatcher = dispatcher;

if (!opts.NoBuiltins)
{
    // BuiltinModels.AttachStandardModels does the nvidia-smi VRAM probe + attaches
    // the C# IModel zoo. Slow on cold start (a few seconds for the probe) but
    // matches the app's behaviour. Skip via --no-builtins when you only need
    // SQL-defined models (much faster startup).
    try
    {
        BuiltinModels.AttachStandardModels(catalog, modelsDir);
        Console.WriteLine($"Built-in models: attached ({catalog.Models?.Entries.Count ?? 0} entries)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Built-in models: failed to attach — {ex.Message}");
        Console.Error.WriteLine("(continuing without — pass --no-builtins to skip this step entirely)");
    }
}
else
{
    catalog.Models = new ModelCatalog(modelDirectory: modelsDir);
    Console.WriteLine("Built-in models: skipped (--no-builtins).");
}

if (!opts.NoRehydrate)
{
    ModelRehydrationReport rep = await catalog.RehydrateModelsAsync(CancellationToken.None);
    Console.WriteLine($"Rehydrated SQL models: loaded={rep.Loaded} skipped={rep.Skipped}");
    foreach (string w in rep.Warnings) Console.Error.WriteLine($"  warning: {w}");
}

foreach ((string tableName, string tablePath) in opts.Tables)
{
    if (!File.Exists(tablePath))
    {
        Console.Error.WriteLine($"Table mount failed: '{tablePath}' does not exist.");
        return 2;
    }
    try
    {
        catalog.Add(new TableDescriptor(tableName, tablePath));
        Console.WriteLine($"Mounted: {tableName} -> {tablePath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Mount failed: {tableName} -> {tablePath}: {ex.Message}");
        return 2;
    }
}

foreach (string applyPath in opts.ApplySql)
{
    string applySql = await File.ReadAllTextAsync(applyPath);
    Console.WriteLine($"Applying: {applyPath}");
    try
    {
        // PlanAsync auto-routes CREATE MODEL to the registrar without persisting
        // back to the catalog file when the body comes via the in-process Plan
        // path (caller controls persistence via the CatalogStore). The probe
        // intentionally doesn't pin the change — it goes away when the process
        // exits.
        await catalog.PlanAsync(applySql);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  failed: {ex.Message}");
        return 2;
    }
}

RecentActivityLog? activityLog = opts.ActivityLogCapacity is int cap
    ? new RecentActivityLog(cap)
    : null;

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    cts.Cancel();
    e.Cancel = true;

    // On Ctrl+C, dump the engine's live state for hang investigations.
    // We CANNOT use DatumActivity.CurrentStack() here — that walks
    // Activity.Current, which is AsyncLocal-backed and therefore null on
    // this console-control thread. Instead we use the cross-thread
    // LiveSnapshot from the RecentActivityLog (if enabled), which mirrors
    // every started-and-not-yet-stopped Activity into a concurrent set
    // readable from any thread.
    Console.Error.WriteLine();
    Console.Error.WriteLine("── Live operators at cancel (oldest first → leaf where execution is parked) ──");
    if (activityLog is null)
    {
        Console.Error.WriteLine("  (no listener — pass --activity-log to enable live tracking)");
    }
    else
    {
        ActivityFrame[] live = activityLog.LiveSnapshot();
        if (opts.ActivitySources.Count > 0)
        {
            live = live.Where(f => opts.ActivitySources.Contains(f.SourceName)).ToArray();
        }
        if (live.Length == 0)
        {
            Console.Error.WriteLine("  (no operators open — engine is idle)");
        }
        else
        {
            foreach (ActivityFrame f in live)
            {
                string parent = f.ParentName is null ? "" : $"  ⇐ {f.ParentName}";
                Console.Error.WriteLine($"  [{ShortSource(f.SourceName)}] {f.Name,-36}{parent}  elapsed {f.Elapsed.TotalSeconds,8:F3}s");
            }
        }

        RecentActivityEntry[] events = opts.ActivitySources.Count > 0
            ? activityLog.Snapshot(opts.ActivitySources.ToArray())
            : activityLog.Snapshot();
        Console.Error.WriteLine();
        Console.Error.WriteLine($"── Recent activity (last {events.Length} completed spans) ──");
        foreach (RecentActivityEntry ev in events)
        {
            string parent = ev.ParentName is null ? "" : $"  ⇐ {ev.ParentName}";
            Console.Error.WriteLine(
                $"  {ev.StartedAt:HH:mm:ss.fff} [{ShortSource(ev.SourceName)}] {ev.Name,-36}{parent}  {ev.Duration.TotalMilliseconds,8:F2} ms");
        }
    }
};

for (int sqlIdx = 0; sqlIdx < sqls.Count; sqlIdx++)
{
    string sql = sqls[sqlIdx];

    Console.WriteLine();
    Console.WriteLine($"SQL[{sqlIdx + 1}/{sqls.Count}]: {Truncate(sql.Trim().ReplaceLineEndings(" "), 200)}");
    Console.WriteLine(new string('─', 80));

    for (int iter = 1; iter <= opts.Repeat; iter++)
    {
        if (opts.Repeat > 1 || sqls.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"── SQL {sqlIdx + 1}/{sqls.Count} Run {iter}/{opts.Repeat} {new string('─', 60)}");
        }

    IQueryPlan plan;
    try
    {
        plan = await catalog.PlanAsync(sql);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Plan error: {ex.Message}");
        if (ex.InnerException is not null) Console.Error.WriteLine($"  inner: {ex.InnerException.Message}");
        return 1;
    }

    Stopwatch sw = Stopwatch.StartNew();
    long totalRows = 0, printedRows = 0;
    bool headerPrinted = false;
    bool truncated = false;

    try
    {
        await foreach (RowBatch batch in plan.ExecuteAsync(cts.Token))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                totalRows++;

                if (!headerPrinted)
                {
                    Console.WriteLine(string.Join('\t', row.ColumnNames));
                    Console.WriteLine(new string('─', 80));
                    headerPrinted = true;
                }

                if (opts.PrintAll || printedRows < opts.Limit)
                {
                    Console.WriteLine(FormatRow(row, batch.Arena, catalog.SidecarRegistry));
                    printedRows++;
                }
                else
                {
                    truncated = true;
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"(cancelled after {sw.Elapsed.TotalSeconds:F2}s)");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Execution error (run {iter}/{opts.Repeat}): {ex.GetType().Name}: {ex.Message}");
        Exception? cur = ex.InnerException;
        while (cur is not null)
        {
            Console.Error.WriteLine($"  inner ({cur.GetType().Name}): {cur.Message}");
            cur = cur.InnerException;
        }
        if (ex.StackTrace is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.StackTrace);
        }
        return 1;
    }

    sw.Stop();

    Console.WriteLine();
    if (!headerPrinted)
    {
        Console.WriteLine($"(0 rows in {sw.Elapsed.TotalSeconds:F3}s)");
    }
    else if (truncated)
    {
        Console.WriteLine($"({printedRows:N0} of {totalRows:N0} rows printed in {sw.Elapsed.TotalSeconds:F3}s — add --all to show all)");
    }
    else
    {
        Console.WriteLine($"({totalRows:N0} rows in {sw.Elapsed.TotalSeconds:F3}s)");
    }
    } // end Run iter loop
} // end SQL idx loop

// Release catalog handles before tearing down the scratch dir.
catalog.Dispose();

if (catalogRootIsScratch)
{
    try { Directory.Delete(catalogRoot, recursive: true); }
    catch { /* leave it for the next gc / temp-cleanup pass */ }
}

return 0;

static string FormatRow(Row row, Arena arena, SidecarRegistry? registry)
{
    StringBuilder builder = new();
    for (int i = 0; i < row.FieldCount; i++)
    {
        if (i > 0) builder.Append('\t');
        builder.Append(FormatValue(row[i], arena, registry));
    }
    return builder.ToString();
}

static string FormatValue(DataValue value, Arena arena, SidecarRegistry? registry)
{
    if (value.IsNull) return "NULL";
    if (value.IsArray)
    {
        return FormatArrayPreview(value, arena, registry);
    }
    try
    {
        return FormatScalar(value, arena, registry);
    }
    catch (Exception ex)
    {
        // Real .datum tables exercise corners the probe doesn't need to
        // pretty-print (sidecar-bound strings, multi-arena boundaries,
        // etc.). Fall back to a marker rather than crashing the whole
        // print loop — the cell value is interesting only for the row it
        // came from, not the whole query.
        return $"<{value.Kind}:err:{ex.GetType().Name}>";
    }
}

static string FormatScalar(DataValue value, Arena arena, SidecarRegistry? registry)
{
    return value.Kind switch
    {
        DataKind.Boolean => value.AsBoolean() ? "true" : "false",
        DataKind.UInt8 => value.AsUInt8().ToString(),
        DataKind.Int8 => value.AsInt8().ToString(),
        DataKind.UInt16 => value.AsUInt16().ToString(),
        DataKind.Int16 => value.AsInt16().ToString(),
        DataKind.UInt32 => value.AsUInt32().ToString(),
        DataKind.Int32 => value.AsInt32().ToString(),
        DataKind.UInt64 => value.AsUInt64().ToString(),
        DataKind.Int64 => value.AsInt64().ToString(),
        DataKind.Float32 => value.AsFloat32().ToString("G"),
        DataKind.Float64 => value.AsFloat64().ToString("G"),
        DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
        DataKind.DateTime => value.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
        DataKind.Time => value.AsTime().ToString("HH:mm:ss"),
        DataKind.Duration => value.AsDuration().ToString(),
        DataKind.Uuid => value.AsUuid().ToString(),
        DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena, registry),
        DataKind.Struct => "<Struct>",
        _ when value.IsByteArrayKind => $"<{value.Kind}>",
        DataKind.Image => "<Image>",
        _ => $"<{value.Kind}>",
    };
}

static string FormatArrayPreview(DataValue value, Arena arena, SidecarRegistry? registry)
{
    // For shape-style typed-numeric arrays, inline a short preview so output
    // like inference.onnx_inspect (which emits Int32[] shape rows) is readable.
    // Cap at 8 elements to keep wide tables sane.
    const int Preview = 8;
    try
    {
        return value.Kind switch
        {
            DataKind.Int32 => RenderPrimitive(value.AsArraySpan<int>(arena), Preview, i => i.ToString()),
            DataKind.Int64 => RenderPrimitive(value.AsArraySpan<long>(arena), Preview, i => i.ToString()),
            DataKind.Float32 => RenderPrimitive(value.AsArraySpan<float>(arena), Preview, f => f.ToString("G6")),
            DataKind.Float64 => RenderPrimitive(value.AsArraySpan<double>(arena), Preview, f => f.ToString("G6")),
            DataKind.Boolean => RenderPrimitive(value.AsArraySpan<byte>(arena), Preview, b => b != 0 ? "true" : "false"),
            DataKind.String => RenderStrings(value.AsStringArray(arena, registry), Preview),
            _ => $"<{value.Kind}[]>",
        };
    }
    catch
    {
        // Arena/registry mismatch on some kinds (e.g. struct arrays without
        // a sidecar); fall back to the opaque marker rather than blowing up
        // a debugging probe.
        return $"<{value.Kind}[]>";
    }
}

static string RenderPrimitive<T>(ReadOnlySpan<T> span, int preview, Func<T, string> render)
{
    int n = span.Length;
    if (n == 0) return "[]";
    StringBuilder sb = new();
    sb.Append('[');
    int show = Math.Min(n, preview);
    for (int i = 0; i < show; i++)
    {
        if (i > 0) sb.Append(", ");
        sb.Append(render(span[i]));
    }
    if (n > preview) sb.Append($", ...({n - preview} more)");
    sb.Append(']');
    return sb.ToString();
}

static string RenderStrings(string[] arr, int preview)
{
    if (arr.Length == 0) return "[]";
    StringBuilder sb = new();
    sb.Append('[');
    int show = Math.Min(arr.Length, preview);
    for (int i = 0; i < show; i++)
    {
        if (i > 0) sb.Append(", ");
        sb.Append('\'').Append(arr[i]).Append('\'');
    }
    if (arr.Length > preview) sb.Append($", ...({arr.Length - preview} more)");
    sb.Append(']');
    return sb.ToString();
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

static string ShortSource(string fullName) => fullName switch
{
    "DatumIngest.Operators" => "op",
    "DatumIngest.Scalars" => "fn",
    _ => fullName,
};

sealed record Options(
    string? Sql,
    string? SqlFile,
    string? CatalogRoot,
    string? ModelsDir,
    bool NoBuiltins,
    bool NoRehydrate,
    List<string> ApplySql,
    List<(string Name, string Path)> Tables,
    int Limit,
    bool PrintAll,
    int Repeat,
    List<string> AlsoSql,
    int? ActivityLogCapacity,
    HashSet<string> ActivitySources)
{
    // Convenience aliases for datasets the author always has available
    // on this machine. Keep the canonical mappings here so flags like
    // --coco / --crimes "just work" instead of forcing the full
    // --table name=path verbosity on every invocation.
    private const string CocoPath = @"E:\Datasets\COCO2017\test2017.datum";
    private const string CrimesPath = @"E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.datum";

    public static Options Parse(string[] args)
    {
        string? sql = null;
        string? sqlFile = null;
        string? catalogRoot = null;
        string? modelsDir = null;
        bool noBuiltins = false;
        bool noRehydrate = false;
        List<string> applySql = new();
        List<(string, string)> tables = new();
        int limit = 100;
        bool printAll = false;
        int repeat = 1;
        List<string> alsoSql = new();
        int? activityLogCapacity = null;
        HashSet<string> activitySources = new(StringComparer.Ordinal);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--sql-file":
                    if (++i >= args.Length) throw new ArgumentException("--sql-file requires a path.");
                    sqlFile = args[i];
                    break;
                case "--catalog-root":
                    if (++i >= args.Length) throw new ArgumentException("--catalog-root requires a path.");
                    catalogRoot = args[i];
                    break;
                case "--models-dir":
                    if (++i >= args.Length) throw new ArgumentException("--models-dir requires a path.");
                    modelsDir = args[i];
                    break;
                case "--apply":
                    if (++i >= args.Length) throw new ArgumentException("--apply requires a path.");
                    applySql.Add(args[i]);
                    break;
                case "--table":
                    if (++i >= args.Length) throw new ArgumentException("--table requires <name>=<path>.");
                    tables.Add(ParseTableSpec(args[i]));
                    break;
                case "--coco":
                    tables.Add(("test2017", CocoPath));
                    break;
                case "--crimes":
                    tables.Add(("crimes", CrimesPath));
                    break;
                case "--no-builtins":
                    noBuiltins = true;
                    break;
                case "--no-rehydrate":
                    noRehydrate = true;
                    break;
                case "--limit":
                    if (++i >= args.Length) throw new ArgumentException("--limit requires a count.");
                    if (!int.TryParse(args[i], out limit) || limit < 0)
                        throw new ArgumentException($"--limit must be a non-negative integer, got '{args[i]}'.");
                    break;
                case "--all":
                    printAll = true;
                    break;
                case "--repeat":
                    if (++i >= args.Length) throw new ArgumentException("--repeat requires a count.");
                    if (!int.TryParse(args[i], out repeat) || repeat < 1)
                        throw new ArgumentException($"--repeat must be a positive integer, got '{args[i]}'.");
                    break;
                case "--also":
                    if (++i >= args.Length) throw new ArgumentException("--also requires a SQL string.");
                    alsoSql.Add(args[i]);
                    break;
                case "--activity-log":
                    // Optional integer arg — peek at the next token. Default 100
                    // when omitted or when the next token is another flag.
                    if (i + 1 < args.Length
                        && !args[i + 1].StartsWith('-')
                        && int.TryParse(args[i + 1], out int parsedCap)
                        && parsedCap > 0)
                    {
                        i++;
                        activityLogCapacity = parsedCap;
                    }
                    else
                    {
                        activityLogCapacity = 100;
                    }
                    break;
                case "--activity-source":
                    if (++i >= args.Length) throw new ArgumentException("--activity-source requires a name.");
                    activitySources.Add(args[i] switch
                    {
                        "op" => "DatumIngest.Operators",
                        "fn" => "DatumIngest.Scalars",
                        string s => s,
                    });
                    break;
                default:
                    if (sql is not null) throw new ArgumentException($"Unexpected argument: {arg}");
                    sql = arg;
                    break;
            }
        }

        if (sql is null && sqlFile is null)
            throw new ArgumentException("Missing SQL — pass it as a positional argument or via --sql-file.");
        if (sql is not null && sqlFile is not null)
            throw new ArgumentException("Specify either positional SQL or --sql-file, not both.");

        return new Options(sql, sqlFile, catalogRoot, modelsDir, noBuiltins, noRehydrate, applySql, tables, limit, printAll, repeat, alsoSql, activityLogCapacity, activitySources);
    }

    private static (string, string) ParseTableSpec(string spec)
    {
        int eq = spec.IndexOf('=');
        if (eq <= 0 || eq == spec.Length - 1)
        {
            throw new ArgumentException($"--table expects <name>=<path>, got '{spec}'.");
        }
        return (spec[..eq], spec[(eq + 1)..]);
    }
}
