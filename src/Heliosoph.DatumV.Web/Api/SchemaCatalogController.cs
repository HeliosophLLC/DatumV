using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Web.Dtos.Schema;
using Microsoft.AspNetCore.Mvc;

namespace Heliosoph.DatumV.Web.Api;

/// <summary>
/// Read-only schema catalog endpoint backing the Catalog Explorer side
/// panel. Returns every queryable table with its columns and indexes in
/// a single nested document — the panel renders a tree so a single round
/// trip is the natural shape.
/// </summary>
/// <remarks>
/// Live updates flow through <see cref="Heliosoph.DatumV.Web.Hubs.CatalogHub"/>:
/// the client refetches on TableCreated / TableAltered / TableDropped /
/// IndexCreated / IndexDropped / SchemaCreated / SchemaDropped /
/// ViewCreated / ViewAltered / ViewDropped kinds.
/// </remarks>
[ApiController]
[Route("api/schema")]
public sealed class SchemaCatalogController(TableCatalog catalog) : ControllerBase
{
    /// <summary>
    /// Returns every queryable table with its full column + index metadata.
    /// </summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<SchemaCatalogDto>> GetCatalog(CancellationToken cancellationToken)
    {
        List<TableEntryDto> tables = [];
        foreach (ITableProvider provider in catalog)
        {
            Schema providerSchema;
            try
            {
                providerSchema = provider.GetSchema();
            }
            catch
            {
                // A provider whose schema introspection throws can't be
                // surfaced — skip it rather than failing the whole call.
                continue;
            }

            (string schemaName, string kind) = Classify(provider.QualifiedName);

            ColumnEntryDto[] columns = new ColumnEntryDto[providerSchema.Columns.Count];
            for (int i = 0; i < providerSchema.Columns.Count; i++)
            {
                ColumnInfo col = providerSchema.Columns[i];
                columns[i] = new ColumnEntryDto(
                    Ordinal: i + 1,
                    Name: col.Name,
                    DataType: col.Kind.ToString(),
                    IsArray: col.IsArray,
                    IsNullable: col.Nullable,
                    IsPrimaryKey: col.IsPrimaryKey);
            }

            // Indexes: null on virtual/temp/system providers (those don't
            // route through a flat-file backend). Empty list is fine for
            // the wire — UI renders "no indexes" the same way.
            IReadOnlyList<IndexDescriptor>? indexDescriptors =
                catalog.GetTableIndexes(provider.QualifiedName.ToString());
            IndexEntryDto[] indexes;
            if (indexDescriptors is null || indexDescriptors.Count == 0)
            {
                indexes = [];
            }
            else
            {
                indexes = new IndexEntryDto[indexDescriptors.Count];
                for (int i = 0; i < indexDescriptors.Count; i++)
                {
                    IndexDescriptor idx = indexDescriptors[i];
                    indexes[i] = new IndexEntryDto(
                        Name: idx.Name,
                        Columns: idx.Columns,
                        IsUnique: idx.IsUnique,
                        Kind: idx.Kind.ToString());
                }
            }

            tables.Add(new TableEntryDto(
                Schema: schemaName,
                Name: provider.QualifiedName.Name,
                Kind: kind,
                Columns: columns,
                Indexes: indexes));
        }

        // Surface registered views alongside tables. View columns come from
        // a static QuerySchemaResolver pass over the stored body — the same
        // resolver the LSP runs for hover / completion — so the Catalog
        // Explorer tree shows the projection users will see when they query
        // the view. A body whose dependency isn't yet on the catalog
        // degrades to an empty column list rather than failing the whole
        // request.
        QuerySchemaResolver viewResolver = new(catalog, catalog.Functions);
        foreach (ViewDescriptor view in catalog.Views.Entries)
        {
            ColumnEntryDto[] viewColumns;
            try
            {
                ResolvedQuerySchema resolved = await viewResolver
                    .ResolveProjectionAsync(view.Body, view.QualifiedName.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                viewColumns = new ColumnEntryDto[resolved.Columns.Count];
                for (int i = 0; i < resolved.Columns.Count; i++)
                {
                    ResolvedColumn col = resolved.Columns[i];
                    viewColumns[i] = new ColumnEntryDto(
                        Ordinal: i + 1,
                        Name: col.ColumnName,
                        DataType: col.Kind.ToString(),
                        IsArray: col.IsArray,
                        IsNullable: col.Nullable,
                        IsPrimaryKey: false);
                }
            }
            catch
            {
                viewColumns = [];
            }

            tables.Add(new TableEntryDto(
                Schema: view.SchemaName,
                Name: view.Name,
                Kind: "VIEW",
                Columns: viewColumns,
                Indexes: []));
        }

        // Stable ordering so the UI doesn't reshuffle between refetches.
        tables.Sort(static (a, b) =>
        {
            int s = string.Compare(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase);
            return s != 0 ? s : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new SchemaCatalogDto(tables);
    }

    // Replicates the internal classification used by the
    // information_schema views — kept private to this controller so the
    // engine assembly can keep its variant internal.
    private static (string Schema, string Kind) Classify(QualifiedName name)
    {
        if (string.Equals(name.Schema, "information_schema", StringComparison.OrdinalIgnoreCase))
            return ("information_schema", "VIEW");
        if (string.Equals(name.Schema, "system", StringComparison.OrdinalIgnoreCase))
            return ("system", "VIEW");
        if (string.Equals(name.Schema, "system", StringComparison.OrdinalIgnoreCase))
            return ("system", "VIEW");
        return (name.Schema, "BASE TABLE");
    }
}
