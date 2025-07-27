namespace DatumIngest.Server;

/// <summary>
/// Operations that can be performed on the server engine, each subject
/// to role-based authorization via <see cref="ServerCapability"/>.
/// </summary>
public enum ServerOperation
{
    /// <summary>Execute a SQL query and stream result rows.</summary>
    Query,

    /// <summary>Inspect the schema of a registered table.</summary>
    Schema,

    /// <summary>Generate a query execution plan.</summary>
    Explain,

    /// <summary>Compute statistics on query results.</summary>
    Stats,

    /// <summary>Add a new data source to the catalog.</summary>
    AddSource,

    /// <summary>Remove a data source from the catalog.</summary>
    RemoveSource,

    /// <summary>Reload the catalog from its backing store.</summary>
    ReloadCatalog,

    /// <summary>Build, load, or remove indexes.</summary>
    ManageIndexes,

    /// <summary>List active sessions on the server.</summary>
    ListSessions,

    /// <summary>Cancel a running query on another session.</summary>
    KillQuery,

    /// <summary>Cancel the active query on the caller's own session.</summary>
    CancelQuery,

    /// <summary>Shut down the server process.</summary>
    Shutdown,
}
