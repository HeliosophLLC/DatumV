using System.Security.Claims;

namespace Heliosoph.DatumV.Web.Hosting;

// Shortcuts onto ClaimsPrincipal for the claims we read everywhere. Each
// returns string.Empty rather than null so consumers don't need ?? "" at
// the call site. Add new shortcuts here as patterns repeat; resist adding
// one-off variants — claims are still freely accessible via User.FindFirstValue.
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.Identity?.Name ?? string.Empty;
}
