#if false
namespace DatumIngest.Catalog;

/// <summary>
/// A named collection of virtual tables that can be addressed using schema-qualified
/// SQL references (e.g. <c>information_schema.tables</c>). Each virtual schema
/// exposes zero or more <see cref="IVirtualTableSource"/> instances that produce
/// metadata rows at query time.
/// </summary>
public interface IVirtualSchema
{
    /// <summary>
    /// The schema name used in SQL (e.g. <c>information_schema</c>, <c>datum_catalog</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The names of all virtual tables in this schema.
    /// </summary>
    IReadOnlyList<string> TableNames { get; }

    /// <summary>
    /// Attempts to resolve a virtual table by name within this schema.
    /// </summary>
    /// <param name="tableName">The unqualified table name (e.g. <c>tables</c>, <c>columns</c>).</param>
    /// <returns>The virtual table source, or <see langword="null"/> if no such table exists.</returns>
    IVirtualTableSource? TryResolve(string tableName);
}
#endif