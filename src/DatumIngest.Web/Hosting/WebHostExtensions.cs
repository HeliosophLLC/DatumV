using DatumIngest.Web.Hubs;

namespace DatumIngest.Web.Hosting;

public static class WebHostExtensions
{
    public static IServiceCollection AddDatumIngestWeb(this IServiceCollection services, WebHostOptions options)
    {
        services.AddSingleton(options);
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
        app.MapControllers();
        app.MapHub<StreamHub>("/hubs/stream");
        app.MapFallbackToFile("index.html");
        return app;
    }
}
