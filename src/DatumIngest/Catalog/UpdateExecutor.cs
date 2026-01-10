using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the <c>UPDATE</c>-statement pipeline for
/// <see cref="TableCatalog.Plan(Statement)"/>.
/// </summary>
/// <remarks>
/// PR11a shipped parse + plan-time validation. PR11c wires the
/// executor: a single scan of the target with the WHERE predicate
/// accumulates per-row SET expression results, then dispatches the
/// page-COW rewrite via <see cref="ITableProvider.UpdateRows"/>.
/// <c>UPDATE … FROM</c> (joins) lands in PR11d.
/// </remarks>
internal static class UpdateExecutor
{
    public static void Execute(TableCatalog catalog, UpdateStatement update)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(update);

        ITableProvider provider = Validate(catalog, update);

        if (update.From is not null || update.Joins is { Count: > 0 })
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': UPDATE … FROM / JOIN is PR11d; " +
                "this build only supports plain UPDATE.");
        }

        ExecuteAsync(catalog, provider, update).GetAwaiter().GetResult();
    }

    private static async Task ExecuteAsync(
        TableCatalog catalog,
        ITableProvider provider,
        UpdateStatement update)
    {
        Schema schema = provider.GetSchema();

        // Resolve SET column names → schema column indices once. The
        // validator already verified that every name resolves and no
        // PK column is touched.
        (int columnIndex, Expression valueExpression)[] setBindings =
            new (int, Expression)[update.Assignments.Count];
        for (int i = 0; i < update.Assignments.Count; i++)
        {
            ColumnAssignment a = update.Assignments[i];
            int idx = FindColumnIndex(schema, a.ColumnName);
            setBindings[i] = (idx, a.Value);
        }

        // workArena outlives every per-batch arena because UpdateRows
        // resolves non-inline SET results against it after the scan
        // loop completes. Disposed at end of method.
        using Arena workArena = new();
        ExpressionEvaluator evaluator = new(
            functions: catalog.Functions,
            sidecarRegistry: catalog.SidecarRegistry);

        List<RowUpdateRequest> requests = new();
        long liveRowIndex = 0;

        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                Arena sourceArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++, liveRowIndex++)
                {
                    Row row = batch[r];

                    // WHERE — predicate-only frame; results are inline
                    // bools so target = source is fine.
                    if (update.Where is not null)
                    {
                        EvaluationFrame predFrame = new(
                            row,
                            sourceArena,
                            sourceArena,
                            outerRow: null,
                            sidecarRegistry: catalog.SidecarRegistry,
                            types: null);
                        if (!await evaluator.EvaluateAsBooleanAsync(
                                update.Where, predFrame, CancellationToken.None).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }

                    // SET — each expression's result lands in workArena
                    // so non-inline payloads survive past this batch.
                    EvaluationFrame setFrame = new(
                        row,
                        sourceArena,
                        workArena,
                        outerRow: null,
                        sidecarRegistry: catalog.SidecarRegistry,
                        types: null);

                    Dictionary<int, DataValue> rowValues = new(setBindings.Length);
                    foreach ((int columnIndex, Expression valueExpression) in setBindings)
                    {
                        DataValue raw = await evaluator.EvaluateAsync(
                            valueExpression, setFrame, CancellationToken.None).ConfigureAwait(false);

                        ColumnInfo target = schema.Columns[columnIndex];
                        DataValue coerced = CoerceForUpdate(
                            raw, sourceArena, workArena, target, update.TableName);
                        rowValues[columnIndex] = coerced;
                    }

                    requests.Add(new RowUpdateRequest(liveRowIndex, rowValues));
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        if (requests.Count > 0)
        {
            provider.UpdateRows(requests, workArena);
        }
    }

    /// <summary>
    /// Coerces a SET-expression result to the target column's
    /// <see cref="DataKind"/>, mirroring the coercion path INSERT uses
    /// for source values from <c>SELECT</c>: extract a CLR scalar from
    /// the source <see cref="DataValue"/>, then route through
    /// <see cref="LiteralCoercion.Coerce"/> which handles widening,
    /// fit-checked narrowing, and NOT NULL enforcement uniformly.
    /// </summary>
    private static DataValue CoerceForUpdate(
        DataValue source,
        IValueStore sourceStore,
        Arena targetArena,
        ColumnInfo target,
        string tableName)
    {
        if (source.IsNull)
        {
            if (!target.Nullable)
            {
                throw new QueryPlanException(
                    $"UPDATE '{tableName}': column '{target.Name}' is NOT NULL; SET expression " +
                    "produced a null value.");
            }
            return DataValue.Null(target.Kind);
        }

        object scalar = source.Kind switch
        {
            DataKind.String => source.AsString(sourceStore),
            DataKind.Boolean => source.AsBoolean(),
            DataKind.Uuid => source.AsUuid(),
            DataKind.Int8 => source.AsInt8(),
            DataKind.Int16 => source.AsInt16(),
            DataKind.Int32 => source.AsInt32(),
            DataKind.Int64 => source.AsInt64(),
            DataKind.UInt8 => source.AsUInt8(),
            DataKind.UInt16 => source.AsUInt16(),
            DataKind.UInt32 => source.AsUInt32(),
            DataKind.UInt64 => source.AsUInt64(),
            DataKind.Float32 => source.AsFloat32(),
            DataKind.Float64 => source.AsFloat64(),
            DataKind.Date => source.AsDate(),
            DataKind.Time => source.AsTime(),
            DataKind.DateTime => source.AsDateTime(),
            DataKind.Duration => source.AsDuration(),
            _ => throw new QueryPlanException(
                $"UPDATE '{tableName}': SET expression for column '{target.Name}' " +
                $"produced kind {source.Kind} which is not yet supported (composite kinds — " +
                "Struct, typed arrays, Image / Audio / ByteArray — land in a follow-up)."),
        };

        return LiteralCoercion.Coerce(scalar, target, targetArena, target.Name);
    }

    /// <summary>
    /// Resolves the target provider, asserts writability, and validates
    /// the SET assignment list against the target schema.
    /// </summary>
    /// <remarks>
    /// Validation rules (PR11a):
    /// <list type="bullet">
    ///   <item>Target table must be registered in the catalog.</item>
    ///   <item>Target provider must opt in to UPDATE
    ///     (<see cref="ITableProvider.CanUpdateRows"/>), which excludes
    ///     read-only sources (CSV, Parquet, JSON, system tables).</item>
    ///   <item>Every SET column must exist on the target schema
    ///     (case-insensitive), and no column may be assigned twice in the
    ///     same statement.</item>
    ///   <item>No SET column may belong to the table's PRIMARY KEY —
    ///     PK column updates are explicitly out of scope; users must
    ///     <c>DELETE</c> and re-<c>INSERT</c> to change a row's PK.</item>
    /// </list>
    /// </remarks>
    private static ITableProvider Validate(TableCatalog catalog, UpdateStatement update)
    {
        if (!catalog.TryGetTable(update.TableName, out ITableProvider? provider))
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': table is not registered in the catalog.");
        }
        if (!provider.CanUpdateRows)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': provider type {provider.GetType().Name} " +
                "does not support row updates (CanUpdateRows = false).");
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

        return provider;
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
