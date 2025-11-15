using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Builds a <see cref="LanguageServerManifest"/> from a live <see cref="TableCatalog"/>
/// and <see cref="FunctionRegistry"/>. Used by the interactive shell to feed the
/// language server's completion / diagnostics / hover providers without going
/// through the offline JSON manifest workflow.
/// </summary>
/// <remarks>
/// Function signatures emit only names plus their category flags (aggregate /
/// window / table-valued); parameter lists, return types, and descriptions are
/// not currently exposed by the runtime <see cref="FunctionRegistry"/>, so those
/// fields stay empty / null. The completion provider only needs names to suggest
/// candidates — richer metadata can come later if hover docs are wired up.
/// </remarks>
public static class CatalogManifestBuilder
{
    /// <summary>
    /// Constructs a manifest snapshotting every registered table's name and
    /// schema, plus every registered function name in the given registry.
    /// </summary>
    public static LanguageServerManifest Build(TableCatalog catalog, FunctionRegistry functions)
    {
        List<TableSchemaEntry> tables = new();
        foreach (ITableProvider provider in catalog)
        {
            Schema schema = provider.GetSchema();
            List<TableColumnEntry> columns = new(schema.Columns.Count);
            foreach (ColumnInfo column in schema.Columns)
            {
                columns.Add(new TableColumnEntry
                {
                    Name = column.Name,
                    Kind = column.Kind.ToString(),
                    Nullable = column.Nullable,
                });
            }
            tables.Add(new TableSchemaEntry
            {
                Name = provider.Name,
                Columns = columns,
            });
        }

        List<FunctionSignature> functionSigs = new();
        foreach (string name in functions.ScalarFunctionNames)
        {
            functionSigs.Add(new FunctionSignature { Name = name, Parameters = [] });
        }
        foreach (string name in functions.AggregateFunctionNames)
        {
            functionSigs.Add(new FunctionSignature { Name = name, Parameters = [], IsAggregate = true });
        }
        foreach (string name in functions.WindowFunctionNames)
        {
            functionSigs.Add(new FunctionSignature { Name = name, Parameters = [], IsWindowFunction = true });
        }
        foreach (string name in functions.TableValuedFunctionNames)
        {
            functionSigs.Add(new FunctionSignature { Name = name, Parameters = [], IsTableValued = true });
        }

        // Keywords list = clause keywords + bool/null literals. Type names and
        // date-part names are intentionally not in this list — Monaco draws
        // them from separate Monarch arrays for distinct coloring, and the
        // completion provider can pick them up via its own logic.
        List<string> keywords = new(SqlKeywordRegistry.ClauseKeywords.Count + SqlKeywordRegistry.BoolNullKeywords.Count);
        keywords.AddRange(SqlKeywordRegistry.ClauseKeywords);
        keywords.AddRange(SqlKeywordRegistry.BoolNullKeywords);

        return new LanguageServerManifest
        {
            Tables = tables,
            Functions = functionSigs,
            Keywords = keywords,
        };
    }
}
