using System.Security.Claims;

namespace DatumIngest.Web.Hosting;

// Which compute node owns this request's catalog data. Used by
// ICatalogServiceFactory.ForNode to dispatch to the right backend.
// Today: always `Local` — in-process. Tomorrow: derived from a `datum:node`
// claim on the user, or a tenant→node lookup table.
public sealed record ComputeNodeRef(string Id, string Endpoint)
{
    public static readonly ComputeNodeRef Local = new(Id: "local", Endpoint: "");
}

// Default ClaimsPrincipal for desktop / in-process scenarios. Used as the
// CurrentContext baseline so middleware never assigns to a null reference,
// and as the value returned by LocalCurrentContextResolver. The "Local"
// authentication type tags the principal so consumers can distinguish it
// from JWT/cookie/Windows-auth principals when those land.
public static class LocalUser
{
    public const string AuthenticationType = "Local";
    public const string UserId = "local";
    public const string DisplayName = "Local User";

    public static readonly ClaimsPrincipal Principal = new(
        new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId),
                new Claim(ClaimTypes.Name, DisplayName),
            },
            authenticationType: AuthenticationType));
}
