using System.Security.Claims;

namespace Heliosoph.DatumV.Web.Hosting;

// Per-request, immutable to consumers. Populated by ContextResolverMiddleware
// before any controller/hub runs. Inject this into controllers/services to
// read the user, compute node, and catalog path for the current request.
//
// User is a standard ClaimsPrincipal — same surface ASP.NET auth middleware
// populates `HttpContext.User` with, so JWT / cookie / Windows-auth lands
// without translation. Consumers extract specifics via `User.FindFirstValue(
// ClaimTypes.NameIdentifier)` etc.
public interface ICurrentContext
{
    ClaimsPrincipal User { get; }

    // Which compute node owns this request's catalog data. Today's
    // single-node setup always resolves to ComputeNodeRef.Local.
    ComputeNodeRef Node { get; }

    // The catalog path resolved for this request (e.g. catalog root +
    // tenant/user subdirectory). For the current local-only setup this is
    // just CatalogRootPath.
    string CatalogPath { get; }
}

// Mutable concrete surface — resolvers + middleware populate; consumers read
// through ICurrentContext. Same instance resolves for both within a request's
// DI scope. Public so resolver implementations outside this assembly (future
// auth providers) can write to it.
public sealed class CurrentContext : ICurrentContext
{
    public ClaimsPrincipal User { get; set; } = LocalUser.Principal;
    public ComputeNodeRef Node { get; set; } = ComputeNodeRef.Local;
    public string CatalogPath { get; set; } = string.Empty;
}
