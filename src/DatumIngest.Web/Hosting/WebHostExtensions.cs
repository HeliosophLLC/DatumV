using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.Inference;
using DatumIngest.DatasetLibrary;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;
using DatumIngest.Models.Calibration;
using DatumIngest.Models.Python;
using DatumIngest.Pooling;
using DatumIngest.Web.Catalog;
using DatumIngest.Web.Compute;
using DatumIngest.Web.Conversation;
using DatumIngest.Web.Execution;
using DatumIngest.Web.Hubs;
using DatumIngest.Web.Llm;
using DatumIngest.Web.Lsp;
using DatumIngest.Web.ModelLibrary;
using DatumIngest.Web.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DatumIngest.Web.Hosting;

public static class WebHostExtensions
{
    public static IServiceCollection AddDatumIngestWeb(this IServiceCollection services, WebHostOptions options)
    {
        services.AddSingleton(options);

        // Local-catalog mode: open one TableCatalog on top of the configured
        // root directory, run startup migrations, keep it alive for the
        // process lifetime. SaaS mode skips both — ICatalogService would
        // route to remote nodes and per-principal catalogs would be
        // provisioned out-of-band.
        if (options.ManageLocalCatalog)
        {
            if (string.IsNullOrWhiteSpace(options.CatalogRootPath))
            {
                throw new InvalidOperationException(
                    $"{nameof(WebHostOptions)}.{nameof(WebHostOptions.ManageLocalCatalog)} is true but " +
                    $"{nameof(WebHostOptions.CatalogRootPath)} is not set. Either set the root path or " +
                    $"flip ManageLocalCatalog to false for SaaS-style provisioning.");
            }

            // Pulls in PoolBacking (singleton) + Pool (transient) +
            // FunctionRegistry etc. that TableCatalog needs. Idempotent
            // re-registration is safe but we only call it on the local path.
            services.AddDatumIngest();

            string catalogRootPath = options.CatalogRootPath;
            bool registerBuiltinModels = options.RegisterBuiltinModels;
            string? modelsDirectory = options.ModelsDirectory;

            services.AddSingleton<TableCatalog>(sp =>
            {
                Pool pool = sp.GetRequiredService<Pool>();
                Directory.CreateDirectory(catalogRootPath);
                string catalogFile = Path.Combine(catalogRootPath, CatalogStore.DefaultFileName);
                TableCatalog catalog = new(pool, catalogFile);

                // Wire the process-singleton inference dispatcher so
                // CREATE MODEL, inference.devices(), and the rest of
                // the inference toolkit can resolve their backend.
                // Without this, every inference.* TVF throws "no
                // InferenceDispatcher is configured on this host."
                catalog.InferenceDispatcher = sp.GetRequiredService<IInferenceDispatcher>();

                // Settings-side override (set via the Settings UI, persisted
                // to settings.json) beats the host-config value. If neither
                // is set, ModelCatalog falls back to $DATUM_MODELS env var,
                // then to %LOCALAPPDATA%/DatumIngest/models.
                string? effectiveModelsDir =
                    StartupSettingsLoader.LoadModelsDirectory(catalogRootPath)
                    ?? modelsDirectory;

                // Attach the model subsystem (catalog manifest, calibration,
                // residency, system.* providers) before any hosted service
                // runs. ModelHost uses VramBudgetResolver internally (one
                // nvidia-smi shell-out) and sets catalog.Models. Registrations
                // are cheap — model loads are lazy via the residency manager,
                // triggered on first chat send via LlmDriverHolder.
                if (registerBuiltinModels)
                {
                    ModelHost.AttachTo(catalog, effectiveModelsDir);
                }

                return catalog;
            });

            // TableCatalog implements ICatalogActiveVersionLookup directly —
            // its DeclaredModels rows carry the catalog provenance the
            // lookup needs. Replace the null default from AddModelLibrary.
            services.AddSingleton<ICatalogActiveVersionLookup>(
                sp => sp.GetRequiredService<TableCatalog>());

            services.AddHostedService<CatalogInitializationService>();

            // Dataset catalog mount + bind. Runs after the model catalog
            // init so the TableCatalog is fully ready before the dataset
            // schemas mount.
            services.AddHostedService<DatasetCatalogInitializationService>();

            // Language-intelligence host. Singleton because it owns one
            // LanguageService instance + the catalog-event subscriptions.
            // Hosted-service shim eagerly resolves it at startup so the
            // initial manifest build + event subscription happen before
            // any DDL or LSP request can fire — without this, the first
            // resolve happens on the first /api/lang/* call and misses
            // any DDL that ran during startup migrations.
            services.AddSingleton<LanguageManifestService>();
            services.AddHostedService<LanguageManifestStartupService>();

            // Catalog change broadcaster. Subscribes to the in-process
            // CatalogEvents bus at startup and fans each commit out to
            // every connected CatalogHub client. The service is registered
            // only as a hosted service (no consumer needs to inject it),
            // and runs once for the catalog's lifetime.
            services.AddHostedService<CatalogEventBroadcastService>();

            // Catalog directory watcher. Sits alongside the DDL broadcaster
            // but listens to the filesystem instead of the in-process event
            // bus — fires OnFilesChanged when an external editor / git /
            // hand-edit modifies the catalog tree so the Project Explorer
            // panel can refetch.
            services.AddHostedService<CatalogDirectoryWatcher>();

            // Model lifecycle + calibration observers. Both forward
            // engine-side events to the CatalogHub so the status-bar
            // chips (residency, calibration) can render live state
            // without polling. Singleton because they hold an
            // IHubContext (itself a singleton) and have no per-request
            // state; the registration service is hosted-only and
            // runs at startup to wire them into the catalog's
            // observer fan-out.
            services.AddSingleton<IModelLifecycleObserver, SignalRResidencyObserver>();
            services.AddSingleton<ICalibrationObserver, SignalRCalibrationObserver>();
            services.AddHostedService<ModelObserverRegistrationService>();

            // Streaming SQL execution. Scoped so each request gets its own
            // service instance — its only mutable state today is the
            // catalog reference, which is itself a singleton, but keeping
            // the service scoped leaves room for per-request state (e.g.
            // a request-bound tracer / principal) without churning the
            // surface.
            services.AddScoped<QueryStreamService>();

            // Chat LLM wiring intentionally skipped for the first release.
            // The SQL-LLM migration left the conversation agent's prompt
            // assembly double-wrapping chat templates (the agent builds a
            // templated string via LlamaChatTemplate; the SQL-defined model
            // body then wraps that string again via llama.cpp's native
            // template engine — model sees gibberish) and routes streaming
            // through ProceduralModelAdapter's single-chunk fallback. The
            // front-end's chat dock entry is marked disabled in panels/
            // registry.ts to match. Re-register these three singletons
            // once the agent is rewired against the ChatCompleter task
            // contract (see project_procedural_udf_model_followups.md for
            // the streaming-sink design).
            //
            //   services.AddSingleton<LlmDriverHolder>();
            //   services.AddSingleton<Messages.IMessageGraph, Messages.MessageGraph>();
            //   services.AddSingleton<Conversation.IConversationRegistry, Conversation.ConversationRegistry>();
            //   services.AddSingleton<Conversation.IConversationAgent, Conversation.ConversationAgent>();
        }

        // Per-request context. CurrentContext is the writable concrete; ICurrentContext
        // is the read-only surface consumers inject. Both resolve to the same instance
        // for a given request via the factory lambda.
        services.AddScoped<CurrentContext>();
        services.AddScoped<ICurrentContext>(sp => sp.GetRequiredService<CurrentContext>());
        services.AddSingleton<ICurrentContextResolver, LocalCurrentContextResolver>();

        // Compute boundary. The factory is singleton (routing is stateless);
        // ICatalogService is scoped — resolved once per request, lives through it,
        // released at end. Swap UnboundCatalogServiceFactory for an in-process+gRPC
        // router when actual catalog ops are wired.
        services.AddSingleton<ICatalogServiceFactory, UnboundCatalogServiceFactory>();
        services.AddScoped<ICatalogService>(sp =>
        {
            var ctx = sp.GetRequiredService<ICurrentContext>();
            var factory = sp.GetRequiredService<ICatalogServiceFactory>();
            return factory.ForNode(ctx.Node);
        });

        // Per-user settings. Scoped because the file path resolves from the
        // request's principal/catalog. Today a single LocalUser; tomorrow
        // each user gets their own settings.json under their compute node.
        services.AddScoped<ISettingsService, LocalSettingsService>();

        // Model catalog: manifest reader, HF Hub HTTP client, license
        // acceptance, and the download orchestrator. AddModelLibrary lives in
        // the DatumIngest core assembly — tests and CLI consumers can pull
        // the same surface without dragging in the Web project. The Web host
        // then registers a SignalR-backed progress reporter that bridges
        // core download events to connected hub clients.
        ModelLibraryOptions modelLibraryOptions = new(
            CatalogRootPath: options.CatalogRootPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DatumIngest"),
            ModelsDirectory: options.ModelsDirectory
                ?? Environment.GetEnvironmentVariable("DATUM_MODELS")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DatumIngest", "models"));
        services.AddModelLibrary(modelLibraryOptions);

        services.AddSingleton<IDownloadProgressReporter, SignalRDownloadProgressReporter>();
        // Python-env install events fan out to clients via the same hub
        // pattern. PythonEnvironmentManager picks this up through its
        // optional IPythonEnvironmentReporter ctor param.
        services.AddSingleton<IPythonEnvironmentReporter, SignalRPythonEnvironmentReporter>();
        // Replace the default NullModelInstaller (registered by
        // AddModelLibrary) with the catalog-backed one. CREATE MODEL only
        // makes sense when ManageLocalCatalog is true — without a
        // TableCatalog singleton, the constructor can't resolve. Guarding
        // here also keeps SaaS-mode hosts (where catalogs are remote) from
        // crashing at resolve time on an entry with installSql.
        if (options.ManageLocalCatalog)
        {
            services.AddSingleton<IModelInstaller, CatalogBackedModelInstaller>();

            // Dataset library: manifest reader, path resolver, and the
            // download + install pipeline. Lives inside the local-catalog
            // block because DatasetDownloadService.RunIngestJobAsync needs
            // the engine's Pool + IEnumerable<IFileFormat> registered by
            // AddDatumIngest above — and SaaS hosts don't have a local
            // catalog to land .datum files into anyway. Replaces the
            // default NullDatasetDownloadProgressReporter (registered
            // inside AddDatasetLibrary) with a SignalR-backed adapter,
            // and the default DefaultKeepRawDownloadsPolicy with a
            // settings.json-backed one so user preferences flow through.
            string datasetCatalogRoot = options.CatalogRootPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DatumIngest");
            // Settings-side override (set via the Settings UI, persisted to
            // settings.json) beats the host-config value. If neither is
            // set, the cascade falls through to $DATUM_DATASETS and the
            // per-user default.
            string? effectiveDatasetsDir =
                StartupSettingsLoader.LoadDatasetsDirectory(datasetCatalogRoot)
                ?? options.DatasetsCacheDirectory
                ?? Environment.GetEnvironmentVariable("DATUM_DATASETS")
                ?? Path.Combine(datasetCatalogRoot, "datasets-cache");
            DatasetLibraryOptions datasetLibraryOptions = new(
                CatalogRootPath: datasetCatalogRoot,
                DatasetsCacheDirectory: effectiveDatasetsDir);
            services.AddDatasetLibrary(datasetLibraryOptions);
            services.AddSingleton<IDatasetDownloadProgressReporter,
                Web.DatasetLibrary.SignalRDatasetDownloadProgressReporter>();
            services.AddSingleton<IKeepRawDownloadsPolicy,
                Web.DatasetLibrary.SettingsBackedKeepRawDownloadsPolicy>();
        }

        services.AddControllers()
            .AddJsonOptions(o =>
            {
                // Serialize enums as their camelCase string names so the
                // wire format is human-readable and NSwag can emit TS string
                // unions instead of opaque numeric enums.
                o.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
            });
        services.AddSignalR();
        // StreamHub takes IConversationAgent in its constructor; SignalR
        // resolves the hub via DI on every incoming connection, including
        // connections that only carry server→client download-progress
        // pushes. Chat is intentionally disabled (see the commented LLM
        // wiring block above), but the hub still needs to be constructable
        // or every WebSocket connect to /hubs/stream fails with
        // InvalidOperationException and the server closes the connection —
        // dropping the download-progress channel along with it. Register a
        // null stand-in via TryAdd so a real registration (when chat is
        // re-enabled) wins.
        services.TryAddSingleton<IConversationAgent, NullConversationAgent>();
        services.AddOpenApiDocument(s =>
        {
            s.DocumentName = "v1";
            s.Title = "DatumIngest.Web";
            s.Version = "v1";
        });
        return services;
    }

    public static WebApplication UseDatumIngestWeb(this WebApplication app)
    {
        app.UseStaticFiles();
        app.UseOpenApi(c => c.Path = "/openapi/{documentName}.json");
        // Populate ICurrentContext before any controller runs.
        app.UseMiddleware<ContextResolverMiddleware>();
        app.MapControllers();
        app.MapHub<StreamHub>("/hubs/stream");
        app.MapHub<CatalogHub>("/hubs/catalog");
        app.MapFallbackToFile("index.html");
        return app;
    }
}
