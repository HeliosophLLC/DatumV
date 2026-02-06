using System.Text.Json.Serialization;
using DatumIngest.Catalog;
using DatumIngest.Models;
using DatumIngest.Pooling;
using DatumIngest.Web.Compute;
using DatumIngest.Web.Hubs;
using DatumIngest.Web.Llm;
using DatumIngest.Web.ModelLibrary;
using DatumIngest.Web.Settings;

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

                // Attach the standard model zoo before any hosted service runs.
                // BuiltinModels uses VramBudgetResolver internally (one
                // nvidia-smi shell-out) and sets catalog.Models. Registrations
                // are cheap — model loads are lazy via the residency manager,
                // triggered later by LlmStartupService.
                if (registerBuiltinModels)
                {
                    BuiltinModels.AttachStandardModels(catalog, modelsDirectory);
                }

                return catalog;
            });

            services.AddHostedService<CatalogInitializationService>();

            // Chat LLM wiring. Holder is the singleton consumers depend on;
            // the hosted service sets it during StartAsync after the model is
            // loaded. ILlmDriver resolves through the holder so any consumer
            // (IConversationAgent etc.) gets a fully-loaded driver or a clear
            // "not initialised yet" error.
            if (registerBuiltinModels)
            {
                services.AddSingleton<LlmDriverHolder>();
                services.AddSingleton<ILlmDriver>(sp =>
                    sp.GetRequiredService<LlmDriverHolder>().Current);
                services.AddHostedService<LlmStartupService>();

                services.AddSingleton<Messages.IMessageGraph, Messages.MessageGraph>();
                services.AddSingleton<Conversation.IConversationAgent, Conversation.ConversationAgent>();
            }
        }

        // Per-request context. CurrentContext is the writable concrete; ICurrentContext
        // is the read-only surface consumers inject. Both resolve to the same instance
        // for a given request via the factory lambda.
        services.AddScoped<CurrentContext>();
        services.AddScoped<ICurrentContext>(sp => sp.GetRequiredService<CurrentContext>());
        services.AddSingleton<ICurrentContextResolver, LocalCurrentContextResolver>();

        // Compute boundary. The factory is singleton (routing is stateless);
        // ICatalogService is scoped â€” resolved once per request, lives through it,
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

        // Model catalog: manifest reader (singleton — catalog.json is content
        // shipped with the app), HF Hub HTTP client, license acceptance, and
        // the download orchestrator. HfHubClient takes a long-lived HttpClient
        // from IHttpClientFactory; downloads are multi-GB streams and a fresh
        // socket per download is fine.
        services.AddSingleton<IManifestStore, ManifestStore>();
        services.AddSingleton<ILicenseAcceptanceService, LicenseAcceptanceService>();
        services.AddHttpClient<HfHubClient>();
        services.AddSingleton<IModelDownloadService, ModelDownloadService>();

        // AddApplicationPart so controllers are discovered when DatumIngest.Web
        // is referenced by a non-MVC entry assembly (e.g. DatumIngest.Client).
        // Without this, MVC's default scan only sees the entry assembly's controllers.
        services.AddControllers()
            .AddApplicationPart(typeof(WebHostExtensions).Assembly)
            .AddJsonOptions(o =>
            {
                // Serialize enums as their camelCase string names so the
                // wire format is human-readable and NSwag can emit TS string
                // unions instead of opaque numeric enums.
                o.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
            });
        services.AddSignalR();
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
        app.MapFallbackToFile("index.html");
        return app;
    }
}
