namespace DatumIngest.Web.Dtos.Schema;

// Response payload for GET /api/schema/catalog: the full set of user-visible
// tables together with each table's columns and indexes. Built for the
// Catalog Explorer side panel; not intended as a query-engine surface.
// Scope is small enough today that a single nested document is cheaper to
// render than three parallel endpoints; revisit if the catalog grows
// thousands of tables.
public sealed record SchemaCatalogDto(IReadOnlyList<TableEntryDto> Tables);

// One queryable table or view. `Schema` is the SQL namespace (public,
// information_schema, datum_catalog, system, models). `Kind` tells the UI
// whether to label this as a base table or a system view so users don't
// confuse the two.
public sealed record TableEntryDto(
    string Schema,
    string Name,
    string Kind,
    IReadOnlyList<ColumnEntryDto> Columns,
    IReadOnlyList<IndexEntryDto> Indexes);

public sealed record ColumnEntryDto(
    int Ordinal,
    string Name,
    string DataType,
    bool IsArray,
    bool IsNullable,
    bool IsPrimaryKey);

public sealed record IndexEntryDto(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    // "Composite" | "FullText" | "Bitmap" | "Bloom" | … — surfaces
    // IndexDescriptor.Kind verbatim. UI groups/labels per value.
    string Kind);
