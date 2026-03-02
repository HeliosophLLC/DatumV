using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Tests.Infra;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Tests;

/// <summary>
/// Base class for tests that need a DI service provider. Creates a fresh
/// <see cref="ServiceCollection"/> per test instance (xUnit creates a new
/// instance per test method), registers core services, and disposes the
/// provider on cleanup.
/// </summary>
/// <remarks>
/// Subclasses can override <see cref="ConfigureServices"/> to add
/// additional registrations before the provider is built.
/// </remarks>
public abstract class ServiceTestBase : IDisposable
{
    private ServiceProvider? _provider;

    /// <summary>
    /// The built service provider. Lazily constructed on first access
    /// so that <see cref="ConfigureServices"/> has a chance to run.
    /// </summary>
    protected ServiceProvider Services => _provider ??= BuildProvider();

    /// <summary>
    /// Override to add additional service registrations.
    /// Called once before the provider is built.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // Core services available to all tests.
        services.AddDatumIngest();
        services.AddFileFormats();

        // ModelLibrary: the manifest store reads models/catalog.json (resolved
        // from the test binary upward), the download service can fetch missing
        // ONNX files from HuggingFace, and a capturing progress reporter lets
        // tests await `EnsureModelDownloadedAsync`. Adds essentially zero cost
        // to tests that don't call the helper — the manifest is lazy and the
        // HTTP client only spins up on first download.
        services.AddLogging();
        services.AddModelLibrary(new ModelLibraryOptions(
            CatalogRootPath: ModelsDirectory,
            ModelsDirectory: ModelsDirectory));
        services.AddSingleton<TestDownloadProgressReporter>();
        services.AddSingleton<IDownloadProgressReporter>(
            sp => sp.GetRequiredService<TestDownloadProgressReporter>());

        ConfigureServices(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolved models directory: the <c>DATUM_MODELS</c> env var when set,
    /// otherwise the per-user fallback under <c>%LOCALAPPDATA%/DatumIngest/models</c>
    /// (Windows) or <c>~/.local/share/DatumIngest/models</c> (Linux/macOS).
    /// Matches <see cref="DatumIngest.Models.ModelCatalog.DefaultModelDirectory"/>
    /// so tests and production code see the same model files. Set
    /// <c>DATUM_MODELS=E:\Path\To\Models</c> in your shell to keep large
    /// model files off the C: drive without changing test code.
    /// </summary>
    public static string ModelsDirectory =>
        Environment.GetEnvironmentVariable("DATUM_MODELS")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DatumIngest",
            "models");

    /// <summary>
    /// Ensures the catalog entry's files are present on disk, downloading
    /// from the configured source (HuggingFace today) when missing. Returns
    /// immediately when the model is already present. Throws
    /// <see cref="InvalidOperationException"/> if the download fails;
    /// callers that want to soft-skip the test on network failure should
    /// catch and bail.
    /// </summary>
    /// <param name="modelId">Catalog id, e.g. <c>"paddleocr-v4-det"</c>.</param>
    /// <param name="timeout">Cap on how long the download can take. Default 5 minutes.</param>
    protected async Task EnsureModelDownloadedAsync(
        string modelId,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        IModelDownloadService dl = GetService<IModelDownloadService>();
        ModelInstallState state = await dl.ProbeAsync(modelId, ct).ConfigureAwait(false);
        if (state is ModelInstallState.Installed or ModelInstallState.Downloaded)
        {
            return;
        }

        // Subscribe to the reporter BEFORE kicking off the download so the
        // terminal event doesn't fire-and-vanish before we're listening.
        TestDownloadProgressReporter reporter = GetService<TestDownloadProgressReporter>();
        using CancellationTokenSource timeoutCts = new(timeout ?? TimeSpan.FromMinutes(5));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        Task terminal = reporter.WaitForTerminalAsync(modelId, linked.Token);

        await dl.InstallAsync(modelId, linked.Token).ConfigureAwait(false);
        await terminal.ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the absolute path to a file inside a downloaded model's
    /// local directory. Convention: <c>&lt;ModelsDirectory&gt;/&lt;modelId&gt;/&lt;fileName&gt;</c>.
    /// Used by tests that need to point at a specific ONNX/tokenizer file
    /// after <see cref="EnsureModelDownloadedAsync"/>.
    /// </summary>
    protected static string GetDownloadedModelPath(string modelId, string fileName) =>
        Path.Combine(ModelsDirectory, modelId, fileName);

    /// <summary>Resolves a service from the test provider.</summary>
    protected T GetService<T>() where T : notnull
        => Services.GetRequiredService<T>();


    
    protected static Row MakeRow(string[] columnNames, params DataValue[] values)
    {
        ColumnLookup columnLookup = new(columnNames);
        return new Row(columnLookup, values);
    }
    
    protected static Row MakeRow(ColumnLookup columnLookup, params DataValue[] values)
    {
        return new Row(columnLookup, values);
    }

    /// <summary>
    /// Creates an empty <see cref="Pool"/>.
    /// </summary>
    /// <returns></returns>
    protected Pool CreatePool() => GetService<Pool>();

    /// <summary>
    /// Creates an empty <see cref="TableCatalog"/> backed by a <see cref="Pool"/>
    /// resolved from the test's DI container. Each test instance gets its own
    /// <see cref="PoolBacking"/> (ServiceCollection is per-test), so catalogs
    /// are isolated across tests without sharing pool state.
    /// </summary>
    protected TableCatalog CreateCatalog()
    {
        return new TableCatalog(CreatePool());
    }

    /// <summary>
    /// Creates a <see cref="TableCatalog"/> from the provided <paramref name="path"/>
    /// </summary>
    /// <param name="path">The path to the catalog.</param>
    protected TableCatalog CreateCatalog(string path, bool allowExplicitTablePaths = false)
    {
        return new TableCatalog(CreatePool(), path, allowExplicitTablePaths: allowExplicitTablePaths);
    }

    /// <summary>
    /// Creates a <see cref="TableCatalog"/> using the supplied <paramref name="pool"/>.
    /// Use this overload when a test reopens the same catalog across multiple <c>using</c>
    /// blocks and needs the pool's lifetime to outlive any individual catalog instance.
    /// </summary>
    protected TableCatalog CreateCatalog(Pool pool)
    {
        return new TableCatalog(pool);
    }
    
    /// <summary>
    /// Creates a <see cref="TableCatalog"/> bound to <paramref name="path"/>
    /// using the supplied <paramref name="pool"/>. Use this overload when a
    /// test reopens the same catalog across multiple <c>using</c> blocks and
    /// needs the pool's lifetime to outlive any individual catalog
    /// instance.
    /// </summary>
    protected TableCatalog CreateCatalog(Pool pool, string path, bool allowExplicitTablePaths = false)
    {
        return new TableCatalog(pool, path, allowExplicitTablePaths: allowExplicitTablePaths);
    }

    /// <summary>
    /// Creates a <see cref="TableCatalog"/> pre-populated with
    /// <see cref="InMemoryTableProvider"/> entries for each supplied
    /// <c>(name, rows)</c> pair.
    /// </summary>
    protected TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = CreateCatalog();

        foreach ((string name, Row[] rows) in tables)
        {
            catalog.Add(new InMemoryTableProvider(CreatePool(), name, rows));
        }

        return catalog;
    }

    protected InMemoryTableProvider CreateInMemoryProvider(string name, Row[] rows)
        => new(CreatePool(), name, rows);

    /// <summary>
    /// Creates a catalog with a single in-memory table from positional <c>object[]</c> rows.
    /// Cells can be CLR primitives (<c>int</c>, <c>string</c>, <c>bool</c>, <see cref="DateTimeOffset"/>,
    /// <see cref="Guid"/>, <c>byte[]</c>, etc.), <see cref="DataValue"/> instances for explicit
    /// control, or <c>null</c>. String cells are stored into the batch's arena at scan time.
    /// </summary>
    /// <example>
    /// <code>
    /// TableCatalog catalog = CreateCatalog("orders",
    ///     columns: ["id", "name", "amount"],
    ///     [1, "alice", 100.0],
    ///     [2, "bob",   200.0]);
    /// </code>
    /// </example>
    protected TableCatalog CreateCatalog(
        string tableName,
        string[] columns,
        params object?[][] rows)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(CreatePool(), tableName, columns, rows));
        return catalog;
    }

    /// <summary>
    /// Creates a standalone <see cref="InMemoryTableProvider"/> from positional
    /// <c>object[]</c> rows. Use when wiring a provider into a non-catalog structure
    /// (e.g. directly into a <c>ScanOperator</c>).
    /// </summary>
    protected InMemoryTableProvider CreateProvider(
        string tableName,
        string[] columns,
        params object?[][] rows)
        => new(CreatePool(), tableName, columns, rows);

    /// <summary>
    /// Creates a minimal execution context suitable for most unit tests.
    /// Uses a fresh <see cref="Arena"/> as the value store.
    /// </summary>
    protected DatumIngest.Execution.ExecutionContext CreateExecutionContext(
        FunctionRegistry? functionRegistry = null,
        TableCatalog? catalog = null,
        long? memoryBudgetBytes = null,
        QueryMeter? meter = null,
        AssertionDiagnostics? diagnostics = null,
        int? maxRecursionDepth = null,
        int? batchSize = null,
        int? maxStratifyClasses = null,
        Arena? store = null,
        Pool? pool = null,
        CancellationToken cancellationToken = default)
    {
        pool ??= CreatePool();
        return new(
            cancellationToken,
            functionRegistry ?? FunctionRegistry.CreateDefault(),
            catalog ?? new TableCatalog(pool),
            pool,
            queryMeter: meter,
            memoryBudgetBytes: memoryBudgetBytes,
            store: store)
        {
            AssertionDiagnostics = diagnostics,
            MaxRecursionDepth = maxRecursionDepth ?? 1000,
            BatchSize = batchSize ?? 1024,
            MaxStratifyClasses = maxStratifyClasses ?? 10000
        };
    }

    /// <summary>
    /// Creates a <see cref="Execution.MockOperator"/> that yields the supplied rows.
    /// Backed by an <see cref="InMemoryTableProvider"/> so batches are pool-rented
    /// and carry a valid <see cref="Arena"/> — matching production scan semantics.
    /// </summary>
    protected MockOperator CreateMockOperator(string[] columns, params object?[][] rows)
        => new(CreateProvider("mock", columns, rows));

    /// <summary>
    /// Wraps an existing provider in a <see cref="Execution.MockOperator"/>. Use when
    /// the same row set must feed multiple operators (e.g. running two parallel
    /// operators over identical data for determinism checks).
    /// </summary>
    protected MockOperator CreateMockOperator(InMemoryTableProvider provider)
        => new(provider);

    /// <summary>
    /// Creates a <see cref="Execution.CountingOperator"/> that fires
    /// <paramref name="onRowYielded"/> once per row pulled from the underlying
    /// <see cref="InMemoryTableProvider"/>. Used to verify consumers don't
    /// over-read (e.g. LIMIT correctness).
    /// </summary>
    protected CountingOperator CreateCountingOperator(
        Action onRowYielded,
        string[] columns,
        params object?[][] rows)
        => new(CreateProvider("mock", columns, rows), onRowYielded);

    protected async Task<List<Row>> ExecuteQueryAsync(
        string sql,
        TableCatalog catalog,
        AssertionDiagnostics? diagnostics = null,
        Arena? store = null)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());

        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(catalog: catalog, diagnostics: diagnostics, store: store);

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        return await plan.CollectRowsAsync(context);
    }

    public virtual void Dispose()
    {
        _provider?.Dispose();
        GC.SuppressFinalize(this);
    }
}
