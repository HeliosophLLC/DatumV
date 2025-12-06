using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DevWeb;
using DatumIngest.LanguageServer;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Pooling;

// datum-devweb [--models <path>] [--port <port>] <path>...
//
//   Starts a small ASP.NET Core web app that serves a single-page editor at
//   http://localhost:<port>. POST /api/query runs SQL against the catalog and
//   returns rows + per-cell media as JSON.
//
//   Mirrors datum-shell's argument shape for paths and --models.

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

string? modelsOverride = null;
int port = 5005;
List<string> dataPaths = new();
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg == "--models")
    {
        if (++i >= args.Length)
        {
            Console.Error.WriteLine("--models requires a path.");
            return 1;
        }
        modelsOverride = args[i];
    }
    else if (arg == "--port")
    {
        if (++i >= args.Length || !int.TryParse(args[i], out port))
        {
            Console.Error.WriteLine("--port requires a number.");
            return 1;
        }
    }
    else
    {
        dataPaths.Add(arg);
    }
}

if (dataPaths.Count == 0)
{
    Console.Error.WriteLine("No data paths provided.");
    PrintUsage();
    return 1;
}

Pool pool = new(new PoolBacking());
TableCatalog catalog = new(pool);

ModelCatalog modelCatalog = BuiltinModels.AttachStandardModels(catalog, modelsOverride);
_ = modelCatalog;

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
            Console.Error.WriteLine($"Path not found: {path}");
            catalog.Dispose();
            return 1;
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to register tables: {ex.Message}");
    catalog.Dispose();
    return 1;
}

if (datumFilesAdded == 0)
{
    Console.Error.WriteLine("No .datum files registered.");
    catalog.Dispose();
    return 1;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // Pin content root to the directory that contains the dll. The csproj
    // copies wwwroot/ next to the dll, so static files resolve identically
    // whether the app is launched via `dotnet run`, the coreclr debugger
    // (cwd = workspace root), or a published deployment.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls($"http://localhost:{port}");

WebApplication app = builder.Build();
app.Lifetime.ApplicationStopping.Register(() => catalog.Dispose());

// One query at a time. The catalog isn't documented as concurrent-safe and
// this is a single-developer tool; serialising avoids any cross-query
// arena/store interactions while we keep the engine surface simple.
SemaphoreSlim queryLock = new(1, 1);

// Shared LanguageService initialized from the live catalog. The manifest is a
// snapshot — if we ever support runtime DDL or AddFile, this will need to be
// rebuilt and Initialize'd again. Today the catalog is fixed at boot.
LanguageService languageService = new();
languageService.Initialize(CatalogManifestBuilder.Build(catalog, catalog.Functions));

JsonSerializerOptions jsonOptions = new()
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tables", () =>
{
    List<object> entries = new();
    foreach (ITableProvider provider in catalog)
    {
        long rows;
        try { rows = provider.GetRowCount(); }
        catch { rows = -1; }
        entries.Add(new { name = provider.Name, rows });
    }
    return Results.Json(entries, jsonOptions);
});

app.MapPost("/api/query", async (HttpRequest request, CancellationToken ct) =>
{
    QueryRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<QueryRequest>(
            request.Body, jsonOptions, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }

    if (body is null || string.IsNullOrWhiteSpace(body.Sql))
    {
        return Results.Json(new { error = "sql is required" }, jsonOptions, statusCode: 400);
    }

    int maxRows = body.MaxRows is > 0 ? body.MaxRows.Value : 1000;

    await queryLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        return await ExecuteQuery(catalog, body.Sql, maxRows, jsonOptions, ct).ConfigureAwait(false);
    }
    finally
    {
        queryLock.Release();
    }
});

// Language services. The LanguageService methods are pure over the (immutable)
// manifest, so concurrent calls are fine — no lock here. Each endpoint just
// translates the JSON request body into the matching method call.
app.MapPost("/api/lang/complete", async (HttpRequest request, CancellationToken ct) =>
{
    LangPositionRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<LangPositionRequest>(
            request.Body, jsonOptions, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }
    if (body is null) return Results.Json(new { error = "sql/offset required" }, jsonOptions, statusCode: 400);

    CompletionItem[] items = languageService.GetCompletions(body.Sql ?? string.Empty, body.Offset);
    return Results.Json(items, jsonOptions);
});

app.MapPost("/api/lang/hover", async (HttpRequest request, CancellationToken ct) =>
{
    LangPositionRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<LangPositionRequest>(
            request.Body, jsonOptions, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }
    if (body is null) return Results.Json(new { error = "sql/offset required" }, jsonOptions, statusCode: 400);

    HoverResult? hover = languageService.GetHover(body.Sql ?? string.Empty, body.Offset);
    return Results.Json(hover, jsonOptions);
});

app.MapPost("/api/lang/diagnose", async (HttpRequest request, CancellationToken ct) =>
{
    LangSqlRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<LangSqlRequest>(
            request.Body, jsonOptions, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }
    if (body is null) return Results.Json(new { error = "sql required" }, jsonOptions, statusCode: 400);

    Diagnostic[] diagnostics = languageService.GetDiagnostics(body.Sql ?? string.Empty);
    return Results.Json(diagnostics, jsonOptions);
});

Console.WriteLine($"DatumIngest DevWeb listening on http://localhost:{port}");
Console.WriteLine($"Tables registered: {datumFilesAdded}");
await app.RunAsync().ConfigureAwait(false);
return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: datum-devweb [--models <path>] [--port <port>] <path>...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Each <path> is either a .datum file or a directory of .datum files.");
    Console.Error.WriteLine("  --models <path>   Override the model files directory.");
    Console.Error.WriteLine("                    Falls back to DATUM_MODELS env var, then a per-user default.");
    Console.Error.WriteLine("  --port <port>     HTTP port (default: 5005).");
}

static async Task<IResult> ExecuteQuery(
    TableCatalog catalog,
    string sql,
    int maxRows,
    JsonSerializerOptions jsonOptions,
    CancellationToken ct)
{
    Stopwatch sw = Stopwatch.StartNew();

    IQueryPlan plan;
    try
    {
        plan = catalog.Plan(sql);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, jsonOptions, statusCode: 400);
    }

    SidecarRegistry registry = catalog.SidecarRegistry;
    List<ColumnDescriptor>? schema = null;
    List<List<JsonCell>> rows = new();
    bool truncated = false;
    int columnCount = 0;

    try
    {
        await foreach (RowBatch batch in plan.ExecuteAsync(ct).ConfigureAwait(false))
        {
            Arena arena = batch.Arena;

            if (schema is null)
            {
                IReadOnlyList<string> names = batch.ColumnLookup.ColumnNames;
                columnCount = names.Count;
                schema = new List<ColumnDescriptor>(columnCount);
                if (batch.Count > 0)
                {
                    Row probe = batch[0];
                    for (int i = 0; i < columnCount; i++)
                    {
                        DataValue cell = probe[i];
                        schema.Add(new ColumnDescriptor(names[i], cell.Kind.ToString(), cell.IsArray));
                    }
                }
                else
                {
                    for (int i = 0; i < columnCount; i++)
                    {
                        schema.Add(new ColumnDescriptor(names[i], DataKind.Unknown.ToString(), false));
                    }
                }
            }

            for (int r = 0; r < batch.Count; r++)
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                Row row = batch[r];
                List<JsonCell> cells = new(columnCount);
                for (int c = 0; c < columnCount; c++)
                {
                    cells.Add(WebCellFormatter.Format(row[c], arena, registry));
                }
                rows.Add(cells);
            }

            if (truncated) break;
        }
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "cancelled" }, jsonOptions, statusCode: 499);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = ex.Message, detail = ex.ToString() }, jsonOptions, statusCode: 500);
    }

    sw.Stop();

    return Results.Json(
        new QueryResponse(
            schema ?? new List<ColumnDescriptor>(),
            rows,
            rows.Count,
            truncated,
            sw.Elapsed.TotalMilliseconds),
        jsonOptions);
}

internal sealed record QueryRequest(string Sql, int? MaxRows);

internal sealed record LangPositionRequest(string? Sql, int Offset);

internal sealed record LangSqlRequest(string? Sql);

internal sealed record QueryResponse(
    IReadOnlyList<ColumnDescriptor> Schema,
    IReadOnlyList<IReadOnlyList<JsonCell>> Rows,
    int RowCount,
    bool Truncated,
    double ElapsedMs);

internal sealed record ColumnDescriptor(string Name, string Kind, bool IsArray);
