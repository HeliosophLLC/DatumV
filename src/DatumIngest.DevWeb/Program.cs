using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DevWeb;
using DatumIngest.Execution;
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

// JSON options shared between the minimal-API endpoints and the
// controllers. Defining the instance up front lets us pass it into
// the DI container so AssistantController can pull it out of its
// constructor instead of stitching together its own.
JsonSerializerOptions jsonOptions = new()
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

// Controllers — used for /api/assistant/*. Other routes
// (/api/query, /api/lang/*, /api/tables) stay on minimal API.
// Configure the controllers' JSON output to share the same shape
// so client-side parsing is uniform across both surfaces.
builder.Services
    .AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Engine + assistant service registrations. Catalog is a singleton
// so every controller request shares the same in-memory state; the
// AssistantService wraps it for typed access.
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(jsonOptions);
builder.Services.AddSingleton<DatumIngest.DevWeb.Assistant.IAssistantService,
    DatumIngest.DevWeb.Assistant.AssistantService>();

WebApplication app = builder.Build();
app.MapControllers();

// Seed the assistant schema once at startup so the dev-loop UI never
// races a CREATE TABLE through /api/query/stream on its first turn.
// Idempotent — CREATE TABLE IF NOT EXISTS is a no-op on subsequent
// boots.
{
    DatumIngest.DevWeb.Assistant.IAssistantService _seedService =
        app.Services.GetRequiredService<DatumIngest.DevWeb.Assistant.IAssistantService>();
    await _seedService.EnsureSchemaAsync(CancellationToken.None);
}
app.Lifetime.ApplicationStopping.Register(() => catalog.Dispose());

// Queries run concurrently. The catalog (and per-query ExecutionContext /
// arena) is thread-safe; each request gets its own buffer pool and store,
// and catalog reads / mutations are internally synchronised. This lets the
// UI's per-tab run feature actually parallelise — e.g. a long-running LLM
// streaming query in one tab doesn't block a quick `system_udfs` lookup
// from another.

// Shared LanguageService initialized from the live catalog. The manifest
// is a snapshot of the catalog at a point in time, so DDL run through
// /api/query/stream (CREATE TABLE, DROP TABLE, etc.) won't show up in
// completions / diagnostics until the manifest is rebuilt. The
// streaming endpoint refreshes after every successful batch via
// RefreshLanguageManifest below; LSP endpoints read the live snapshot.
LanguageService languageService = new();
object languageManifestSync = new();
DatumIngest.Manifest.LanguageServerManifest languageManifest =
    CatalogManifestBuilder.Build(catalog, catalog.Functions);
languageService.Initialize(languageManifest);

void RefreshLanguageManifest()
{
    lock (languageManifestSync)
    {
        languageManifest = CatalogManifestBuilder.Build(catalog, catalog.Functions);
        languageService.Initialize(languageManifest);
    }
}

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
    QueryRequestEnvelope envelope;
    try
    {
        envelope = await QueryRequestBinding.ReadAsync(request, jsonOptions, ct);
    }
    catch (JsonException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = $"Bad request: {ex.Message}" }, jsonOptions, statusCode: 400);
    }

    QueryRequest body = envelope.Body;
    if (string.IsNullOrWhiteSpace(body.Sql))
    {
        return Results.Json(new { error = "sql is required" }, jsonOptions, statusCode: 400);
    }

    int maxRows = body.MaxRows is > 0 ? body.MaxRows.Value : 1000;

    return await ExecuteQuery(catalog, body.Sql, maxRows, body.Trace == true, envelope.Parameters, jsonOptions, ct)
        .ConfigureAwait(false);
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

    QueryRequestEnvelope envelope;
    try
    {
        envelope = await QueryRequestBinding.ReadAsync(httpCtx.Request, jsonOptions, ct);
    }
    catch (JsonException ex)
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = $"Bad request: {ex.Message}" }, jsonOptions, ct);
        return;
    }
    catch (InvalidOperationException ex)
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = $"Bad request: {ex.Message}" }, jsonOptions, ct);
        return;
    }

    QueryRequest body = envelope.Body;
    if (string.IsNullOrWhiteSpace(body.Sql))
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = "sql is required" }, jsonOptions, ct);
        return;
    }

    int maxRows = body.MaxRows is > 0 ? body.MaxRows.Value : 1000;
    string sql = body.Sql;

    await ExecuteQueryStreaming(catalog, sql, maxRows, body.Trace == true, envelope.Parameters, jsonOptions, httpCtx);
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

    // DDL (CREATE TABLE / DROP / ALTER / …) run through /api/query/stream
    // mutates the live catalog; the language manifest is a snapshot, so
    // refresh it before serving completion / hover / signature so newly
    // created tables become discoverable on the user's next keystroke.
    RefreshLanguageManifest();
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

    RefreshLanguageManifest();
    HoverResult? hover = languageService.GetHover(body.Sql ?? string.Empty, body.Offset);
    return Results.Json(hover, jsonOptions);
});

app.MapPost("/api/lang/signature", async (HttpRequest request, CancellationToken ct) =>
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

    RefreshLanguageManifest();
    SignatureHelp? sig = languageService.GetSignatureHelp(body.Sql ?? string.Empty, body.Offset);
    return Results.Json(sig, jsonOptions);
});

// Monarch grammar for the SQL dialect. Returned as a JSON object the
// browser feeds into monaco.languages.setMonarchTokensProvider, replacing
// Monaco's built-in 'sql' tokenizer with one that knows about backtick
// template strings (and all other DatumIngest extensions).
// Catalog sidebar reads built-in function signatures from this endpoint.
// `__`-prefixed internal helpers (e.g. __assert_not_null) are filtered out
// the same way they are in completion.
app.MapGet("/api/lang/functions", () =>
{
    var entries = languageManifest.Functions
        .Where(f => !f.Name.StartsWith("__", StringComparison.Ordinal))
        .OrderBy(f => f.Category)
        .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        .Select(f => new
        {
            name = f.Name,
            category = f.Category.ToString(),
            isAggregate = f.IsAggregate,
            isWindow = f.IsWindowFunction,
            isTableValued = f.IsTableValued,
            returnType = f.ReturnType,
            parameters = f.Parameters
                .Select(p => new { name = p.Name, type = p.Kind, optional = p.IsOptional })
                .ToList(),
        })
        .ToList();
    return Results.Json(entries, jsonOptions);
});

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

    RefreshLanguageManifest();
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
    IReadOnlyDictionary<string, ParameterValue> parameters,
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
        if (parameters.Count > 0)
        {
            // Parse → bind → plan. For multi-statement scripts, /api/query
            // delegates to the streaming endpoint; here we assume a single
            // statement and surface a clear error otherwise.
            IReadOnlyList<(DatumIngest.Parsing.Ast.Statement Statement, string SourceText)> stmts =
                DatumIngest.Parsing.SqlParser.ParseBatchWithText(sql);
            if (stmts.Count != 1)
            {
                if (traceCapture is not null) DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
                return Results.Json(
                    new { error = "/api/query with parameters supports a single statement; use /api/query/stream for batches." },
                    jsonOptions, statusCode: 400);
            }
            DatumIngest.Parsing.Ast.Statement bound = ParameterBinder.Bind(stmts[0].Statement, parameters);
            plan = await catalog.PlanAsync(bound, stmts[0].SourceText).ConfigureAwait(false);
        }
        else
        {
            plan = await catalog.PlanAsync(sql).ConfigureAwait(false);
        }
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
                    cells.Add(WebCellFormatter.Format(row[c], arena, registry, batch.Types, batch.TypeIdTranslations));
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
    IReadOnlyDictionary<string, ParameterValue> parameters,
    JsonSerializerOptions jsonOptions,
    HttpContext httpCtx)
{
    CancellationToken ct = httpCtx.RequestAborted;
    Stopwatch sw = Stopwatch.StartNew();

    StringWriter? traceCapture = trace
        ? DatumIngest.Diagnostics.ExecutionTracer.BeginCapture()
        : null;

    // Parse first. Parse errors happen before we've started writing the response
    // body, so we can still return a regular 400 JSON. After the body opens,
    // errors must flow inline as NDJSON `error` events.
    IReadOnlyList<(DatumIngest.Parsing.Ast.Statement Statement, string SourceText)> statements;
    try
    {
        statements = DatumIngest.Parsing.SqlParser.ParseBatchWithText(sql);

        // Parameter binding runs before any execution begins, so missing
        // / unused parameter errors land as 400s rather than mid-stream
        // events. The bound statement list keeps each per-statement
        // source slice alongside its rebound AST.
        if (parameters.Count > 0)
        {
            DatumIngest.Parsing.Ast.Statement[] toBind =
                new DatumIngest.Parsing.Ast.Statement[statements.Count];
            for (int i = 0; i < statements.Count; i++) toBind[i] = statements[i].Statement;
            IReadOnlyList<DatumIngest.Parsing.Ast.Statement> bound =
                ParameterBinder.Bind(toBind, parameters);
            (DatumIngest.Parsing.Ast.Statement, string)[] paired =
                new (DatumIngest.Parsing.Ast.Statement, string)[statements.Count];
            for (int i = 0; i < statements.Count; i++) paired[i] = (bound[i], statements[i].SourceText);
            statements = paired;
        }
    }
    catch (Exception ex)
    {
        if (traceCapture is not null) DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = ex.Message }, jsonOptions, ct);
        return;
    }

    if (statements.Count == 0)
    {
        httpCtx.Response.StatusCode = 400;
        await httpCtx.Response.WriteAsJsonAsync(new { error = "empty SQL input" }, jsonOptions, ct);
        return;
    }

    httpCtx.Response.ContentType = "application/x-ndjson";
    Stream output = httpCtx.Response.Body;
    string sessionId = Guid.NewGuid().ToString("N");
    SidecarRegistry registry = catalog.SidecarRegistry;

    void WriteEvent(object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), jsonOptions);
        output.Write(json, 0, json.Length);
        output.WriteByte((byte)'\n');
        output.Flush();
    }

    WriteEvent(new SessionEvent("session", sessionId));

    // Single execution path: every batch goes through BatchExecutor —
    // procedural blocks (DECLARE/SET/IF/WHILE/BEGIN), single SELECT, single
    // EXEC, multi-statement scripts. LLM chunk streaming is preserved via
    // BatchEventStreamingSink, which translates per-cell IModelStreamingSink
    // chunks into CellChunkBatchEvent emissions on the same event channel.
    bool errorEmitted = false;

    try
    {
        await ExecuteBatchAsync(
            catalog, statements, maxRows, output, jsonOptions, registry, ct, WriteEvent);

        if (traceCapture is not null)
        {
            string traceText = DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
            traceCapture = null;
            if (!string.IsNullOrEmpty(traceText))
            {
                // Trace today is batch-wide rather than per-cell; emit it
                // attached to a synthetic "batch" cellId since it spans the
                // whole run. Clients that filter by cellId can ignore it.
                WriteEvent(new TraceEvent("trace", "batch", traceText));
            }
        }

        sw.Stop();
        WriteEvent(new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds));
    }
    catch (OperationCanceledException)
    {
        WriteEvent(new ErrorEvent("error", null, "cancelled", null));
        errorEmitted = true;
    }
    catch (Exception ex)
    {
        WriteEvent(new ErrorEvent("error", null, ex.Message, ex.ToString()));
        errorEmitted = true;
    }
    finally
    {
        if (traceCapture is not null)
        {
            DatumIngest.Diagnostics.ExecutionTracer.EndCapture(traceCapture);
        }

        if (errorEmitted)
        {
            try
            {
                WriteEvent(new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds));
            }
            catch { /* response stream broken; nothing to do */ }
        }
    }
}

/// <summary>
/// Drives <see cref="BatchExecutor"/>
/// and forwards its <see cref="BatchEvent"/>s onto the NDJSON wire.
/// Cell IDs come from BatchExecutor (monotonic across nested control-flow);
/// each query/exec cell emits its own schema + row events.
/// </summary>
static async Task ExecuteBatchAsync(
    TableCatalog catalog,
    IReadOnlyList<(DatumIngest.Parsing.Ast.Statement Statement, string SourceText)> statements,
    int maxRows,
    Stream output,
    JsonSerializerOptions jsonOptions,
    SidecarRegistry registry,
    CancellationToken ct,
    Action<object> writeEvent)
{
    BatchExecutor executor = new(catalog);

    // Per-cell row-count + schema-emitted state. Re-set on each
    // CellStartedBatchEvent so each cell has its own row budget.
    string? currentCellId = null;
    bool schemaEmitted = false;
    int cellRowCount = 0;
    bool cellTruncated = false;

    ValueTask OnEvent(BatchEvent ev)
    {
        switch (ev)
        {
            case CellStartedBatchEvent started:
                currentCellId = started.CellId;
                schemaEmitted = false;
                cellRowCount = 0;
                cellTruncated = false;
                writeEvent(new CellStartedEvent("cell_started", started.CellId, started.Kind, Sql: null));
                break;

            case CellRowBatchEvent rowEvent:
            {
                if (cellTruncated) break; // drain remaining batches in this cell

                RowBatch batch = rowEvent.Batch;
                Arena arena = batch.Arena;

                if (!schemaEmitted)
                {
                    writeEvent(new SchemaEvent("schema", rowEvent.CellId, BuildSchema(batch)));
                    schemaEmitted = true;
                }

                for (int r = 0; r < batch.Count; r++)
                {
                    if (cellRowCount >= maxRows)
                    {
                        cellTruncated = true;
                        break;
                    }
                    Row row = batch[r];
                    JsonCell[] cells = new JsonCell[batch.ColumnLookup.Count];
                    for (int c = 0; c < batch.ColumnLookup.Count; c++)
                    {
                        cells[c] = WebCellFormatter.Format(row[c], arena, registry, batch.Types, batch.TypeIdTranslations);
                    }
                    writeEvent(new RowEvent("row", rowEvent.CellId, cells));
                    cellRowCount++;
                }
                break;
            }

            case CellChunkBatchEvent chunkEvent:
                // Live model chunk (LLM token, etc.). Translate 1:1 to a
                // wire `chunk` event scoped to the cell that produced it.
                writeEvent(new ChunkWireEvent("chunk", chunkEvent.CellId, chunkEvent.ModelName, chunkEvent.Text));
                break;

            case CellCompletedBatchEvent completed:
                if (cellTruncated)
                {
                    writeEvent(new TruncatedEvent("truncated", completed.CellId, cellRowCount));
                }
                writeEvent(new CellCompletedEvent("cell_completed", completed.CellId, completed.ElapsedMs));
                currentCellId = null;
                break;

            case CellFailedBatchEvent failed:
                writeEvent(new ErrorEvent("error", failed.CellId, failed.Error.Message, failed.Error.ToString()));
                // The exception propagates after this event; the outer
                // try/catch in ExecuteQueryStreaming will not emit a
                // duplicate top-level error because errorEmitted gates it.
                break;
        }
        return ValueTask.CompletedTask;
    }

    // Widen the per-statement source text to nullable so the executor's
    // unified pair signature accepts both the parsed-from-SQL case (always
    // present) and the AST-only case (always null).
    (DatumIngest.Parsing.Ast.Statement, string?)[] pairs = new (DatumIngest.Parsing.Ast.Statement, string?)[statements.Count];
    for (int i = 0; i < statements.Count; i++) pairs[i] = (statements[i].Statement, statements[i].SourceText);
    await executor.RunWithEventsAsync(pairs, OnEvent, ct).ConfigureAwait(false);
}

/// <summary>
/// Builds a <see cref="ColumnDescriptor"/> array from a <see cref="RowBatch"/>'s
/// schema. Falls back to <see cref="DataKind.Unknown"/> on empty batches so the
/// header still renders.
/// </summary>
static ColumnDescriptor[] BuildSchema(RowBatch batch)
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
    return cols;
}

internal sealed record SessionEvent(string Type, string Id);
internal sealed record CellStartedEvent(string Type, string Cell, string Kind, string? Sql);
internal sealed record ChunkWireEvent(string Type, string Cell, string Model, string Text);
internal sealed record SchemaEvent(string Type, string Cell, IReadOnlyList<ColumnDescriptor> Columns);
internal sealed record RowEvent(string Type, string Cell, IReadOnlyList<JsonCell> Cells);
internal sealed record TruncatedEvent(string Type, string Cell, int RowCount);
internal sealed record TraceEvent(string Type, string Cell, string Text);
internal sealed record CellCompletedEvent(string Type, string Cell, double ElapsedMs);
internal sealed record CompleteEvent(string Type, double ElapsedMs);
internal sealed record ErrorEvent(string Type, string? Cell, string Message, string? Detail);

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
