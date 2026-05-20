namespace Heliosoph.DatumV.Web.Hosting;

// Single point where principal + catalog path are resolved per request.
// Swap implementations to change auth/routing without touching controllers.
//   - LocalCurrentContextResolver: always-authenticated "local" user, fixed path
//   - (future) CookieCurrentContextResolver: reads HttpContext.User claims
//   - (future) JwtCurrentContextResolver: tenant-aware claim extraction
public interface ICurrentContextResolver
{
    ValueTask ResolveAsync(HttpContext httpContext, CurrentContext target);
}

internal sealed class LocalCurrentContextResolver(WebHostOptions options) : ICurrentContextResolver
{
    public ValueTask ResolveAsync(HttpContext httpContext, CurrentContext target)
    {
        target.User = LocalUser.Principal;
        target.Node = ComputeNodeRef.Local;
        target.CatalogPath = options.CatalogRootPath
            ?? throw new InvalidOperationException(
                "WebHostOptions.CatalogRootPath must be set by the host (Client or Web standalone).");
        return ValueTask.CompletedTask;
    }
}
