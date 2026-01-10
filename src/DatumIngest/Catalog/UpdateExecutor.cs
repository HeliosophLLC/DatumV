using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the <c>UPDATE</c>-statement pipeline for
/// <see cref="TableCatalog.Plan(Statement)"/>.
/// </summary>
/// <remarks>
/// PR11a covers parse + plan-time validation only. The actual rewrite
/// path lands in PR11b/c (page-COW <c>RewritePages</c> primitive +
/// plain executor). Validation failures throw
/// <see cref="QueryPlanException"/> with a user-actionable message; if
/// validation passes, <see cref="Execute"/> throws
/// <see cref="NotSupportedException"/> with a PR11b/c hint. Tests pin the
/// validation surface so the executor can be slotted in without API churn.
/// </remarks>
internal static class UpdateExecutor
{
    public static void Execute(TableCatalog catalog, UpdateStatement update)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(update);

        Validate(catalog, update);

        throw new NotSupportedException(
            $"UPDATE '{update.TableName}': statement parsed and validated, but " +
            "the rewrite path is not yet implemented. The page-COW " +
            "RewritePages primitive ships in PR11b; the plain UPDATE " +
            "executor in PR11c; UPDATE … FROM in PR11d.");
    }

    /// <summary>
    /// Resolves the target provider, asserts writability, and validates
    /// the SET assignment list against the target schema.
    /// </summary>
    /// <remarks>
    /// Validation rules (PR11a):
    /// <list type="bullet">
    ///   <item>Target table must be registered in the catalog.</item>
    ///   <item>Target provider must be writable
    ///     (<see cref="ITableProvider.CanAppendRows"/> and
    ///     <see cref="ITableProvider.CanDeleteRows"/> both true) — the
    ///     same gate the DELETE / INSERT executors use, which excludes
    ///     read-only sources (CSV, Parquet, JSON, system tables).</item>
    ///   <item>Every SET column must exist on the target schema
    ///     (case-insensitive), and no column may be assigned twice in the
    ///     same statement.</item>
    ///   <item>No SET column may belong to the table's PRIMARY KEY —
    ///     PK column updates are explicitly out of scope; users must
    ///     <c>DELETE</c> and re-<c>INSERT</c> to change a row's PK.</item>
    /// </list>
    /// </remarks>
    private static void Validate(TableCatalog catalog, UpdateStatement update)
    {
        if (!catalog.TryGetTable(update.TableName, out ITableProvider? provider))
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': table is not registered in the catalog.");
        }
        if (!provider.CanAppendRows || !provider.CanDeleteRows)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': provider type {provider.GetType().Name} " +
                "is read-only (CanAppendRows / CanDeleteRows = false).");
        }

        if (update.Assignments.Count == 0)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': SET list is empty.");
        }

        Schema schema = provider.GetSchema();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> pkColumns = schema.PrimaryKeyColumnIndices.Count == 0
            ? new HashSet<int>()
            : new HashSet<int>(schema.PrimaryKeyColumnIndices);

        for (int i = 0; i < update.Assignments.Count; i++)
        {
            string name = update.Assignments[i].ColumnName;

            int columnIndex = FindColumnIndex(schema, name);
            if (columnIndex < 0)
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' does not exist on the target table.");
            }

            if (!seen.Add(name))
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' is assigned more than once in the SET list.");
            }

            if (pkColumns.Contains(columnIndex))
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' is part of the PRIMARY KEY. " +
                    "PK column values are immutable — DELETE and re-INSERT to change a row's primary key.");
            }
        }
    }

    private static int FindColumnIndex(Schema schema, string name)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
}
