using DatumIngest.Catalog;

namespace DatumIngest.Server;

/// <summary>
/// An isolated execution context within a <see cref="Session"/>. Each query
/// context has its own temp table namespace, analogous to a separate SSMS tab.
/// Temp tables created in one context are invisible to all others. Base tables
/// from the session catalog are visible through the context catalog's
/// <see cref="TableCatalog.Parent"/> chain.
/// </summary>
public sealed class QueryContext : IDisposable
{
    private readonly Guid _sessionId;

    /// <summary>
    /// Initializes a new query context with its own isolated temp table overlay.
    /// </summary>
    /// <param name="sessionId">The owning session's identifier (used for temp file paths).</param>
    /// <param name="sessionCatalog">The session's base catalog, set as the parent for fallback resolution.</param>
    /// <param name="label">Human-readable label for debugging (e.g. "Tab 1", "SQL Assistant").</param>
    internal QueryContext(Guid sessionId, TableCatalog sessionCatalog, string label)
    {
        _sessionId = sessionId;
        ContextId = Guid.NewGuid();
        Label = label;
        CreatedAt = DateTimeOffset.UtcNow;

        Catalog = new TableCatalog { Parent = sessionCatalog };
    }

    /// <summary>Gets the unique identifier for this context.</summary>
    public Guid ContextId { get; }

    /// <summary>Gets the human-readable label assigned at creation.</summary>
    public string Label { get; }

    /// <summary>Gets the timestamp when this context was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the layered catalog for this context. Temp tables are registered here;
    /// base tables are resolved through the <see cref="TableCatalog.Parent"/> chain.
    /// </summary>
    public TableCatalog Catalog { get; }

    /// <summary>
    /// Returns the number of temp tables currently held by this context.
    /// Only counts tables registered locally in the context overlay, not
    /// tables inherited from the parent session catalog.
    /// </summary>
    public int TempTableCount => Catalog.LocalTableCount;

    /// <summary>
    /// Returns the temp file directory scoped to this context.
    /// </summary>
    internal string GetTempDirectory()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum_session_{_sessionId:N}",
            $"ctx_{ContextId:N}");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    /// <summary>
    /// Returns the temp file path for a table within this context's isolated directory.
    /// </summary>
    /// <param name="tableName">The logical temp table name.</param>
    /// <returns>The full file path for the temp table's .datum file.</returns>
    internal string GetTempFilePath(string tableName)
    {
        return Path.Combine(GetTempDirectory(), $"{tableName}.datum");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Catalog.Dispose();

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"datum_session_{_sessionId:N}",
            $"ctx_{ContextId:N}");

        if (Directory.Exists(tempDirectory))
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
