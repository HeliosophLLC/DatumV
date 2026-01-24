using DatumIngest.Web.Compute;
using DatumIngest.Web.Hubs;

namespace DatumIngest.Web.Hosting;

public static class WebHostExtensions
{
    public static IServiceCollection AddDatumIngestWeb(this IServiceCollection services, WebHostOptions options)
    {
        services.AddSingleton(options);

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

        // AddApplicationPart so controllers are discovered when DatumIngest.Web
        // is referenced by a non-MVC entry assembly (e.g. DatumIngest.Client).
        // Without this, MVC's default scan only sees the entry assembly's controllers.
        services.AddControllers()
            .AddApplicationPart(typeof(WebHostExtensions).Assembly);
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
