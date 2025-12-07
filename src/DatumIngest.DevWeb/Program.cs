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
long? vramBudgetOverrideBytes = null;
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
    else if (arg == "--vram-budget-gb")
    {
        if (++i >= args.Length ||
            !double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double gb) ||
            gb <= 0)
        {
            Console.Error.WriteLine("--vram-budget-gb requires a positive number (e.g. 18 or 18.5).");
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
    Console.Error.WriteLine("No data paths provided.");
    PrintUsage();
    return 1;
}
else if (dataPaths.Count != 1)
{
    Console.Error.WriteLine("No data paths provided.");
    PrintUsage();
    return 1;
}

TableCatalog catalog = TableCatalog.FromDirectory(dataPaths[0]);

ModelCatalog modelCatalog = BuiltinModels.AttachStandardModels(
    catalog, modelsOverride, vramBudgetBytes: vramBudgetOverrideBytes);

// Surface the resolved budget at startup. Auto-detection queries
// nvidia-smi; if it can't reach a GPU the resolver falls back to a
// conservative default rather than leaving the residency manager
// unlimited (which lets CUDA over-allocate into shared system memory
// transparently and tanks inference latency).
if (modelCatalog.VramBudgetBytes == ModelResidencyManager.UnlimitedBudget)
{
    Console.WriteLine("VRAM budget: unlimited (no eviction; risks shared-RAM spillover)");
}
else
{
    double gb = modelCatalog.VramBudgetBytes / (1024.0 * 1024.0 * 1024.0);
    Console.WriteLine($"VRAM budget: {gb:F1} GB (residency manager evicts LRU when exceeded)");
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
        return await ExecuteQuery(catalog, body.Sql, maxRows, body.Trace == true, jsonOptions, ct)
            .ConfigureAwait(false);
    }
    finally
    {
        queryLock.Release();
    }
});

// Streaming query endpoint. Same request shape as /api/query, but the response
// is a sequence of NDJSON events (one JSON object per line, terminated by '\n')
// emitted as the plan produces output. Event types:
//
//   {type:"session",        id}
//   {type:"cell_started",   cell, kind:"select"|"exec", sql}
//   {type:"schema",         cell, columns:[{name,kind,isArray},...]}    // first batch
//   {type:"chunk",          cell, model, text}                          // streaming model output (live)
//   {type:"row",            cell, cells:[JsonCell,...]}                 // per row
//   {type:"truncated",      cell, rowCount}                             // hit maxRows
//   {type:"trace",          cell, text}                                 // captured trace
//   {type:"cell_completed", cell, elapsedMs}
//   {type:"complete",       elapsedMs}
//   {type:"error",          cell?, message, detail?}
//
// Forward-compat:
//   - Clients MUST ignore unknown event types.
//   - Reserved (not yet emitted): cell_started.kind ∈ {"assign","if","while",...},
//     "breakpoint_hit", "breakpoint_resumed", "step_paused", "scope_changed".
//   - Multi-cell: today every stream emits exactly one cell. When LET / IF /
//     WHILE / multi-statement bodies land, multiple cell_started…cell_completed
//     groups will appear, separated by interleaved events.
app.MapPost("/api/query/stream", async (HttpContext httpCtx) =>
{
    // The streaming sink writes chunk lines synchronously from inside the
    // operator's await chain (the IModelStreamingSink interface is sync).
    // Allow blocking writes on Response.Body so the sink can flush bytes
    // immediately rather than marshalling through a channel.
    Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature? bodyControl =
        httpCtx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
    if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

    CancellationToken ct = httpCtx.RequestAborted;

    QueryRequest? body;
    try
    {
        body = await JsonSerializer.DeserializeAsync<QueryRequest>(
            httpCtx.Request.Body, jsonOptions, ct).ConfigureAwait(false);
    }
    catch (JsonException ex)
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = $"Bad request: {ex.Message}" }, jsonOptions, ct);
        return;
    }

    if (body is null || string.IsNullOrWhiteSpace(body.Sql))
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = "sql is required" }, jsonOptions, ct);
        return;
    }

    int maxRows = body.MaxRows is > 0 ? body.MaxRows.Value : 1000;
    string sql = body.Sql;

    await queryLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        await ExecuteQueryStreaming(catalog, sql, maxRows, body.Trace == true, jsonOptions, httpCtx);
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

// Monarch grammar for the SQL dialect. Returned as a JSON object the
// browser feeds into monaco.languages.setMonarchTokensProvider, replacing
// Monaco's built-in 'sql' tokenizer with one that knows about backtick
// template strings (and all other DatumIngest extensions).
app.MapGet("/api/lang/grammar", () =>
{
    return Results.Json(MonarchGrammarFactory.Build(), jsonOptions);
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
Console.WriteLine($"Tables registered: {catalog.Count}");
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
    Console.Error.WriteLine("  --vram-budget-gb <n>     Override the auto-detected VRAM budget (e.g. 18 or 18.5).");
    Console.Error.WriteLine("  --vram-budget-unlimited  Disable residency eviction (risks shared-RAM spillover).");
    Console.Error.WriteLine("                           Auto-detection queries nvidia-smi and subtracts a 4 GB headroom.");
}

static async Task<IResult> ExecuteQuery(
    TableCatalog catalog,
    string sql,
    int maxRows,
    bool trace,
    JsonSerializerOptions jsonOptions,
    CancellationToken ct)
{
    Stopwatch sw = Stopwatch.StartNew();

    // Optional per-query trace capture. Untraced queries skip this entirely so
    // there is zero tracer overhead for the common case.
    StringWriter? traceCapture = trace
        ? DatumIngest.Diagnostics.ExecutionTracer.BeginCapture()
        : null;

    IQueryPlan plan;
    try
    {
        plan = catalog.Plan(sql);
    }
    catch (Exception ex)
    {
        if (traceCapture is not null)
        {
            DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        }
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
        if (traceCapture is not null)
        {
            DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        }
        return Results.Json(new { error = "cancelled" }, jsonOptions, statusCode: 499);
    }
    catch (Exception ex)
    {
        if (traceCapture is not null)
        {
            DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        }
        return Results.Json(
            new { error = ex.Message, detail = ex.ToString() }, jsonOptions, statusCode: 500);
    }

    sw.Stop();

    string? traceText = null;
    if (traceCapture is not null)
    {
        traceText = DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        if (string.IsNullOrEmpty(traceText)) traceText = null;
    }

    return Results.Json(
        new QueryResponse(
            schema ?? new List<ColumnDescriptor>(),
            rows,
            rows.Count,
            truncated,
            sw.Elapsed.TotalMilliseconds,
            traceText),
        jsonOptions);
}

static async Task ExecuteQueryStreaming(
    TableCatalog catalog,
    string sql,
    int maxRows,
    bool trace,
    JsonSerializerOptions jsonOptions,
    HttpContext httpCtx)
{
    CancellationToken ct = httpCtx.RequestAborted;
    Stopwatch sw = Stopwatch.StartNew();

    StringWriter? traceCapture = trace
        ? DatumIngest.Diagnostics.ExecutionTracer.BeginCapture()
        : null;

    // Plan first. Plan errors happen before we've started writing the response
    // body, so we can still return a regular 400 JSON. After the body opens,
    // errors must flow inline as NDJSON `error` events.
    IQueryPlan plan;
    try
    {
        plan = catalog.Plan(sql);
    }
    catch (Exception ex)
    {
        if (traceCapture is not null) DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = ex.Message }, jsonOptions, ct);
        return;
    }

    httpCtx.Response.ContentType = "application/x-ndjson";
    Stream output = httpCtx.Response.Body;

    string sessionId = Guid.NewGuid().ToString("N");
    string cellId = "c0";
    string cellKind = sql.TrimStart().StartsWith("EXEC", StringComparison.OrdinalIgnoreCase)
        ? "exec"
        : sql.TrimStart().StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            ? "explain"
            : "select";

    void WriteEvent(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), jsonOptions);
        output.Write(json, 0, json.Length);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    SidecarRegistry registry = catalog.SidecarRegistry;
    NdjsonStreamingSink sink = new(output, jsonOptions, cellId);

    bool errorEmitted = false;
    int rowCount = 0;
    bool truncated = false;
    bool schemaEmitted = false;

    try
    {
        WriteEvent(new SessionEvent("session", sessionId));
        WriteEvent(new CellStartedEvent("cell_started", cellId, cellKind, sql));

        await foreach (RowBatch batch in plan.ExecuteAsync(ct, sink).ConfigureAwait(false))
        {
            Arena arena = batch.Arena;

            if (!schemaEmitted)
            {
                IReadOnlyList<string> names = batch.ColumnLookup.ColumnNames;
                ColumnDescriptor[] cols = new ColumnDescriptor[names.Count];
                if (batch.Count > 0)
                {
                    Row probe = batch[0];
                    for (int i = 0; i < names.Count; i++)
                    {
                        DataValue cell = probe[i];
                        cols[i] = new ColumnDescriptor(names[i], cell.Kind.ToString(), cell.IsArray);
                    }
                }
                else
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        cols[i] = new ColumnDescriptor(names[i], DataKind.Unknown.ToString(), false);
                    }
                }
                WriteEvent(new SchemaEvent("schema", cellId, cols));
                schemaEmitted = true;
            }

            for (int r = 0; r < batch.Count; r++)
            {
                if (rowCount >= maxRows)
                {
                    truncated = true;
                    break;
                }
                Row row = batch[r];
                JsonCell[] cells = new JsonCell[batch.ColumnLookup.Count];
                for (int c = 0; c < batch.ColumnLookup.Count; c++)
                {
                    cells[c] = WebCellFormatter.Format(row[c], arena, registry);
                }
                WriteEvent(new RowEvent("row", cellId, cells));
                rowCount++;
            }

            if (truncated) break;
        }

        if (truncated)
        {
            WriteEvent(new TruncatedEvent("truncated", cellId, rowCount));
        }

        if (traceCapture is not null)
        {
            string traceText = DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
            traceCapture = null;
            if (!string.IsNullOrEmpty(traceText))
            {
                WriteEvent(new TraceEvent("trace", cellId, traceText));
            }
        }

        sw.Stop();
        WriteEvent(new CellCompletedEvent("cell_completed", cellId, sw.Elapsed.TotalMilliseconds));
        WriteEvent(new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds));
    }
    catch (OperationCanceledException)
    {
        WriteEvent(new ErrorEvent("error", cellId, "cancelled", null));
        errorEmitted = true;
    }
    catch (Exception ex)
    {
        WriteEvent(new ErrorEvent("error", cellId, ex.Message, ex.ToString()));
        errorEmitted = true;
    }
    finally
    {
        if (traceCapture is not null)
        {
            DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        }

        // Always close the cell + emit complete so the client knows the
        // stream is done, even on error. Skip if we never opened the cell
        // (only happens if WriteEvent itself threw, which we don't recover
        // from anyway).
        if (errorEmitted)
        {
            try
            {
                WriteEvent(new CellCompletedEvent("cell_completed", cellId, sw.Elapsed.TotalMilliseconds));
                WriteEvent(new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds));
            }
            catch { /* response stream broken; nothing to do */ }
        }
    }
}

internal sealed record SessionEvent(string Type, string Id);
internal sealed record CellStartedEvent(string Type, string Cell, string Kind, string Sql);
internal sealed record SchemaEvent(string Type, string Cell, IReadOnlyList<ColumnDescriptor> Columns);
internal sealed record RowEvent(string Type, string Cell, IReadOnlyList<JsonCell> Cells);
internal sealed record TruncatedEvent(string Type, string Cell, int RowCount);
internal sealed record TraceEvent(string Type, string Cell, string Text);
internal sealed record CellCompletedEvent(string Type, string Cell, double ElapsedMs);
internal sealed record CompleteEvent(string Type, double ElapsedMs);
internal sealed record ErrorEvent(string Type, string? Cell, string Message, string? Detail);

internal sealed record QueryRequest(string Sql, int? MaxRows, bool? Trace);

internal sealed record LangPositionRequest(string? Sql, int Offset);

internal sealed record LangSqlRequest(string? Sql);

internal sealed record QueryResponse(
    IReadOnlyList<ColumnDescriptor> Schema,
    IReadOnlyList<IReadOnlyList<JsonCell>> Rows,
    int RowCount,
    bool Truncated,
    double ElapsedMs,
    string? Trace);

internal sealed record ColumnDescriptor(string Name, string Kind, bool IsArray);
