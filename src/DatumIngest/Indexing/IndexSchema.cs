using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Cached schema and row count from a source index. Eliminates the need to open
/// the source file and sample rows for schema inference at query time.
/// </summary>
public sealed class IndexSchema
{
    /// <summary>The column definitions inferred during index creation.</summary>
    public Schema Schema { get; }

    /// <summary>Total number of rows in the source file.</summary>
    public long TotalRowCount { get; }

    /// <summary>
    /// Creates a new index schema.
    /// </summary>
    /// <param name="schema">The column definitions.</param>
    /// <param name="totalRowCount">Total number of rows in the source file.</param>
    public IndexSchema(Schema schema, long totalRowCount)
    {
        Schema = schema;
        TotalRowCount = totalRowCount;
    }
}
