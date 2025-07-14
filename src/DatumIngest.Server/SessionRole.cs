namespace DatumIngest.Server;

/// <summary>
/// Defines the authorization level of a connected session.
/// </summary>
public enum SessionRole
{
    /// <summary>
    /// Standard user with read-only query access: can run queries, inspect
    /// schemas, view explain plans, and compute statistics.
    /// </summary>
    User,

    /// <summary>
    /// Administrator with full control: all user capabilities plus catalog
    /// management (add/remove sources, reload, indexes), session management
    /// (list/kill), and server lifecycle (shutdown).
    /// </summary>
    Admin,
}
