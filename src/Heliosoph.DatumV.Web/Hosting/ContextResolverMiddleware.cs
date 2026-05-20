namespace Heliosoph.DatumV.Web.Hosting;

// Runs once per HTTP request, before controllers, to populate the scoped
// CurrentContext. Hub method invocations need an equivalent — left for the
// hub-context round; the same resolver will plug in there.
public sealed class ContextResolverMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        CurrentContext target,
        ICurrentContextResolver resolver)
    {
        await resolver.ResolveAsync(httpContext, target);
        await next(httpContext);
    }
}
