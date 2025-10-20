using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.Execution;
using DatumIngest.Pooling;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Parsing.Ast;
using DatumIngest.Statistics;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Server;

/// <summary>
/// Executes DDL and DML <see cref="Statement"/> nodes against a query context's catalog
/// and temp table storage. Handles CREATE TEMP TABLE, INSERT, UPDATE, DELETE,
/// ALTER ADD, and DROP TABLE operations.
/// </summary>
internal sealed class StatementExecutor
{
    private readonly Session _session;
    private readonly QueryContext _queryContext;
    private readonly ParallelismBudget? _parallelismBudget;

    /// <summary>
    /// Initializes a new statement executor.
    /// </summary>
    /// <param name="session">The session owning the governor and function registry.</param>
    /// <param name="queryContext">The query context providing the layered catalog and temp file isolation.</param>
    /// <param name="parallelismBudget">Optional global parallelism budget, or <see langword="null"/> for unlimited.</param>
    internal StatementExecutor(Session session, QueryContext queryContext, ParallelismBudget? parallelismBudget = null)
    {
        _session = session;
        _queryContext = queryContext;
        _parallelismBudget = parallelismBudget;
    }

    /// <summary>
    /// Executes a single DDL or DML statement and returns the result.
    /// </summary>
    /// <param name="statement">The parsed statement to execute.</param>
    /// <param name="cancellationToken">Cancellation token for this operation.</param>
    /// <param name="queryMeter">Optional meter for accumulating Query Unit costs.</param>
    /// <param name="parameters">Optional named parameter bindings.</param>
    /// <returns>A <see cref="CommandResult"/> describing the outcome.</returns>
    internal async Task<CommandResult> ExecuteAsync(
        Statement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter = null,
        IReadOnlyDictionary<string, DataValue>? parameters = null)
    {
        return statement switch
        {
            CreateTempTableStatement createStatement =>
                ExecuteCreateTempTable(createStatement),
            CreateTempTableAsSelectStatement createAsSelect =>
                await ExecuteCreateTempTableAsSelectAsync(createAsSelect, cancellationToken, queryMeter, parameters)
                    .ConfigureAwait(false),
            DropTableStatement dropStatement =>
                ExecuteDropTable(dropStatement),
            InsertStatement insertStatement =>
                await ExecuteInsertAsync(insertStatement, cancellationToken, queryMeter, parameters)
                    .ConfigureAwait(false),
            UpdateStatement updateStatement =>
                await ExecuteUpdateAsync(updateStatement, cancellationToken, queryMeter, parameters)
                    .ConfigureAwait(false),
            DeleteStatement deleteStatement =>
                await ExecuteDeleteAsync(deleteStatement, cancellationToken, queryMeter, parameters)
                    .ConfigureAwait(false),
            AlterTableAddColumnStatement alterStatement =>
                ExecuteAlterTableAddColumn(alterStatement),
            AnalyzeTableStatement analyzeStatement =>
                ExecuteAnalyzeTable(analyzeStatement),
            _ => throw new InvalidOperationException(
                $"Statement type {statement.GetType().Name} is not executable as DDL/DML."),
        };
    }

    // ──────────────────── CREATE TEMP TABLE ────────────────────

    private CommandResult ExecuteCreateTempTable(CreateTempTableStatement statement)
    {
        string tableName = statement.TableName;

        if (_queryContext.Catalog.TryResolve(tableName, out _))
        {
            if (statement.IfNotExists)
            {
                return CommandResult.AffectedRows(0, $"Table '{tableName}' already exists.");
            }

            return CommandResult.Error($"Table '{tableName}' already exists.");
        }

        List<ColumnInfo> columns = new(statement.Columns.Count);
        foreach (ColumnDefinition column in statement.Columns)
        {
            DataKind kind = ResolveTypeName(column.TypeName);
            columns.Add(new ColumnInfo(column.Name, kind, column.Nullable));
        }

        Schema schema = new(columns);
        string filePath = GetTempFilePath(tableName);

        // Write an empty .datum file with the declared schema.
        using (DatumFileWriter writer = new(filePath))
        {
            DatumFileSchema datumSchema = DatumFileSchema.FromSchema(schema);
            writer.Initialize(datumSchema);
            writer.Finalize();
        }

        RegisterTempTable(tableName, filePath, statement.PrimaryKeyColumns);
        return CommandResult.AffectedRows(0, $"Created temp table '{tableName}'.");
    }

    private async Task<CommandResult> ExecuteCreateTempTableAsSelectAsync(
        CreateTempTableAsSelectStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        string tableName = statement.TableName;

        if (_queryContext.Catalog.TryResolve(tableName, out _))
        {
            if (statement.IfNotExists)
            {
                return CommandResult.AffectedRows(0, $"Table '{tableName}' already exists.");
            }

            return CommandResult.Error($"Table '{tableName}' already exists.");
        }

        string filePath = GetTempFilePath(tableName);
        long rowCount = await MaterializeQueryToFileAsync(
            statement.Query, filePath, cancellationToken, queryMeter, parameters).ConfigureAwait(false);

        RegisterTempTable(tableName, filePath);

        if (rowCount > 0)
        {
            RebuildTempTableSidecars(tableName, filePath);
        }

        return CommandResult.AffectedRows(rowCount, $"Created temp table '{tableName}' with {rowCount} rows.");
    }

    // ──────────────────── DROP TABLE ────────────────────

    private CommandResult ExecuteDropTable(DropTableStatement statement)
    {
        string tableName = statement.TableName;

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            if (statement.IfExists)
            {
                return CommandResult.AffectedRows(0, $"Table '{tableName}' does not exist.");
            }

            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        _queryContext.Catalog.Unregister(tableName);

        // Best-effort delete of the backing file.
        try
        {
            if (File.Exists(descriptor.FilePath))
            {
                File.Delete(descriptor.FilePath);
            }
        }
        catch (IOException)
        {
            // File may be locked by another reader; ignore.
        }

        return CommandResult.AffectedRows(0, $"Dropped table '{tableName}'.");
    }

    // ──────────────────── INSERT ────────────────────

    private async Task<CommandResult> ExecuteInsertAsync(
        InsertStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        string tableName = statement.TableName;

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        DatumFileSchema schema;
        using (DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath))
        {
            schema = reader.FileSchema;
        }

        List<List<DataValue>> columnBuffers;
        long insertedRows;

        switch (statement.Source)
        {
            case InsertQuerySource querySource:
                (columnBuffers, insertedRows) = await CollectRowsFromQueryAsync(
                    descriptor.FilePath, schema, querySource.Query,
                    statement.ColumnNames, cancellationToken, queryMeter, parameters).ConfigureAwait(false);
                break;

            case InsertValuesSource valuesSource:
                (columnBuffers, insertedRows) = CollectRowsFromValues(
                    schema, valuesSource.Rows, statement.ColumnNames);
                break;

            default:
                return CommandResult.Error($"Unsupported INSERT source: {statement.Source.GetType().Name}.");
        }

        if (insertedRows > 0)
        {
            string? nullViolation = ValidateNotNullConstraints(schema, columnBuffers);
            if (nullViolation is not null)
            {
                return CommandResult.Error(nullViolation);
            }

            if (descriptor.PrimaryKeyColumns is { Count: > 0 } primaryKeyColumns)
            {
                string? violation = ValidatePrimaryKeyUniqueness(
                    descriptor.FilePath, schema, columnBuffers, primaryKeyColumns);

                if (violation is not null)
                {
                    return CommandResult.Error(violation);
                }
            }

            AppendBufferedRows(descriptor.FilePath, schema, columnBuffers, (uint)insertedRows);
            _queryContext.Catalog.MarkAnalysisPending(tableName);

            if (descriptor.Mutability == TableMutability.SessionOwned)
            {
                RebuildTempTableSidecars(tableName, descriptor.FilePath);
            }
        }

        return CommandResult.AffectedRows(insertedRows, $"Inserted {insertedRows} rows into '{tableName}'.");
    }

    /// <summary>
    /// Rebuilds the column statistics manifest for a session-owned table and registers it
    /// on the session catalog. Index sidecars are produced exclusively by
    /// <see cref="DatumIngest.Ingestion.Indexer"/> and are not rebuilt here — the manifest
    /// is sufficient for the query planner's selectivity estimates.
    /// </summary>
    private void RebuildTempTableSidecars(string tableName, string filePath)
    {
        StatisticsCollector statisticsCollector = new();
        using Arena statisticsArena = new(); // TODO: remove when sidecar rebuild is refactored

        Schema schema;
        long totalRowCount = 0;

        using (DatumFileReader reader = DatumFileReader.Open(filePath))
        {
            schema = reader.Schema;
            DatumRowGroupDescriptor[] rowGroups;
            using (FileStream footerStream = File.OpenRead(filePath))
            {
                (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
            }

            string[] columnNames = new string[schema.Columns.Count];
            int[] allIndices = new int[schema.Columns.Count];
            for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
            {
                columnNames[columnIndex] = schema.Columns[columnIndex].Name;
                allIndices[columnIndex] = columnIndex;
            }

            Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
            for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
            {
                nameIndex[columnNames[columnIndex]] = columnIndex;
            }

            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                if (rowGroup.ActiveRowCount == 0) continue;

                DataValue[][] columns = reader.ReadColumns(rowGroupIndex, allIndices);
                int rowCount = (int)rowGroup.RowCount;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (rowGroup.IsRowDeleted(rowIndex)) continue;

                    DataValue[] values = new DataValue[allIndices.Length];
                    for (int columnPosition = 0; columnPosition < allIndices.Length; columnPosition++)
                    {
                        values[columnPosition] = columns[columnPosition][rowIndex];
                    }

                    Row row = new(columnNames, values, nameIndex);
                    statisticsCollector.AddRow(row, statisticsArena);
                    totalRowCount++;
                }
            }
        }

        // Build and register the manifest.
        IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();
        Dictionary<string, DataKind> columnKinds = new(schema.Columns.Count, StringComparer.OrdinalIgnoreCase);
        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            columnKinds[schema.Columns[columnIndex].Name] = schema.Columns[columnIndex].Kind;
        }

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, totalRowCount);

        string manifestPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-manifest";
        ManifestSerializer.WriteToFileAsync(tableName, manifest, manifestPath).GetAwaiter().GetResult();

        _queryContext.Catalog.RegisterManifest(tableName, manifest);
        _queryContext.Catalog.ClearAnalysisPending(tableName);
    }

    private async Task<(List<List<DataValue>> ColumnBuffers, long RowCount)> CollectRowsFromQueryAsync(
        string filePath,
        DatumFileSchema schema,
        QueryExpression query,
        IReadOnlyList<string>? targetColumns,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        if (parameters is not null && parameters.Count > 0)
        {
            query = ParameterBinder.Bind(query, parameters);
        }

        QueryPlanner planner = new(_queryContext.Catalog, _session.FunctionRegistry, _session.VirtualSchemaRegistry);
        LocalBufferPool localBufferPool = GlobalPool.RentLocalBufferPool();

        try
        {
            ExecutionContext context = new(cancellationToken, _session.FunctionRegistry, _queryContext.Catalog, localBufferPool, queryMeter,
                memoryBudgetBytes: _session.Governor.MemoryBudgetBytes, store: new Arena())
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
                MaxStratifyClasses = _session.Governor.MaxStratifyClasses,
            };

            IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, cancellationToken).ConfigureAwait(false);

            // Collect all rows from the query into encoded pages.
            int[] columnMapping = BuildColumnMapping(schema, targetColumns, plan);
            List<List<DataValue>> columnBuffers = new(schema.ColumnCount);
            for (int columnIndex = 0; columnIndex < schema.ColumnCount; columnIndex++)
            {
                columnBuffers.Add(new List<DataValue>());
            }

            long rowCount = 0;
            await foreach (RowBatch batch in plan.ExecuteAsync(context).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
                {
                    Row row = batch[rowIndex];
                    for (int schemaColumnIndex = 0; schemaColumnIndex < schema.ColumnCount; schemaColumnIndex++)
                    {
                        int sourceIndex = columnMapping[schemaColumnIndex];
                        columnBuffers[schemaColumnIndex].Add(sourceIndex >= 0
                            ? row[sourceIndex]
                            : DataValue.Null(schema.Columns[schemaColumnIndex].Kind));
                    }

                    rowCount++;
                }
            }

            return (columnBuffers, rowCount);
        }
        finally
        {
            localBufferPool.Dispose();
        }
    }

    private static (List<List<DataValue>> ColumnBuffers, long RowCount) CollectRowsFromValues(
        DatumFileSchema schema,
        IReadOnlyList<IReadOnlyList<Expression>> rows,
        IReadOnlyList<string>? targetColumns)
    {
        int[] columnMapping = BuildColumnMappingForValues(schema, targetColumns, rows.Count > 0 ? rows[0].Count : 0);

        List<List<DataValue>> columnBuffers = new(schema.ColumnCount);
        for (int columnIndex = 0; columnIndex < schema.ColumnCount; columnIndex++)
        {
            columnBuffers.Add(new List<DataValue>(rows.Count));
        }

        foreach (IReadOnlyList<Expression> valueRow in rows)
        {
            for (int schemaColumnIndex = 0; schemaColumnIndex < schema.ColumnCount; schemaColumnIndex++)
            {
                int sourceIndex = columnMapping[schemaColumnIndex];

                if (sourceIndex >= 0 && sourceIndex < valueRow.Count)
                {
                    DataValue value = EvaluateConstantExpression(valueRow[sourceIndex], schema.Columns[schemaColumnIndex].Kind);
                    columnBuffers[schemaColumnIndex].Add(value);
                }
                else
                {
                    columnBuffers[schemaColumnIndex].Add(DataValue.Null(schema.Columns[schemaColumnIndex].Kind));
                }
            }
        }

        return (columnBuffers, rows.Count);
    }

    // ──────────────────── UPDATE ────────────────────

    /// <summary>
    /// Entry point for UPDATE statements. Validates the target table, enforces the primary key
    /// guard, then dispatches to <see cref="ExecuteSimpleUpdateAsync"/> (single-table expression
    /// SET + optional WHERE) or <see cref="ExecuteFromUpdateAsync"/> (multi-table UPDATE...FROM).
    /// </summary>
    private async Task<CommandResult> ExecuteUpdateAsync(
        UpdateStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        string tableName = statement.TableName;

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        // Reject UPDATE on primary key columns — uniqueness cannot be re-validated
        // after in-place column replacement. Users must DELETE + re-INSERT instead.
        if (descriptor.PrimaryKeyColumns is { Count: > 0 } primaryKeyColumns)
        {
            foreach (ColumnAssignment assignment in statement.Assignments)
            {
                foreach (string primaryKeyColumn in primaryKeyColumns)
                {
                    if (string.Equals(assignment.ColumnName, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        return CommandResult.Error(
                            $"Cannot UPDATE primary key column '{assignment.ColumnName}' in table '{tableName}'. " +
                            "DELETE the rows and re-INSERT with the new key values instead.");
                    }
                }
            }
        }

        if (statement.From is not null)
        {
            return await ExecuteFromUpdateAsync(statement, descriptor, cancellationToken, queryMeter, parameters)
                .ConfigureAwait(false);
        }

        return await ExecuteSimpleUpdateAsync(statement, descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a single-table UPDATE by evaluating each SET assignment expression and the
    /// optional WHERE predicate against every live row, then writing in-place column page
    /// replacements for matching rows. Deleted rows are skipped.
    /// </summary>
    private async Task<CommandResult> ExecuteSimpleUpdateAsync(
        UpdateStatement statement,
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        string tableName = statement.TableName;
        DatumFileSchema schema;
        DatumRowGroupDescriptor[] rowGroups;

        using (DatumFileReader schemaReader = DatumFileReader.Open(descriptor.FilePath))
        {
            schema = schemaReader.FileSchema;
            using FileStream footerStream = File.OpenRead(descriptor.FilePath);
            (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
        }

        int assignmentCount = statement.Assignments.Count;
        int[] assignmentColumnIndices = new int[assignmentCount];
        for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
        {
            assignmentColumnIndices[assignIndex] =
                FindColumnIndex(schema, statement.Assignments[assignIndex].ColumnName);
        }

        int columnCount = schema.ColumnCount;
        int[] allColumnIndices = new int[columnCount];
        for (int index = 0; index < columnCount; index++) allColumnIndices[index] = index;

        // Column names and name-index are shared across all Row instances in a row group.
        string[] columnNames = new string[columnCount];
        for (int index = 0; index < columnCount; index++) columnNames[index] = schema.Columns[index].Name;

        Dictionary<string, int> nameIndex = new(columnCount, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < columnCount; index++) nameIndex[columnNames[index]] = index;

        List<ColumnPageReplacement> replacements = new();
        long updatedRows = 0;

        LocalBufferPool localBufferPool = GlobalPool.RentLocalBufferPool();
        try
        {
            ExpressionEvaluator evaluator = new(_session.FunctionRegistry);

            using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                if (rowGroup.ActiveRowCount == 0) continue;

                int rowCount = (int)rowGroup.RowCount;
                DataValue[][] columns = reader.ReadColumns(rowGroupIndex, allColumnIndices);

                // Pre-fill each assignment column buffer with the original values so that
                // rows not matched by WHERE are written back unchanged.
                DataValue[][] updatedColumns = new DataValue[assignmentCount][];
                for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                {
                    updatedColumns[assignIndex] = new DataValue[rowCount];
                    Array.Copy(columns[assignmentColumnIndices[assignIndex]], updatedColumns[assignIndex], rowCount);
                }

                bool anyChange = false;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (rowGroup.IsRowDeleted(rowIndex)) continue;

                    DataValue[] rowValues = new DataValue[columnCount];
                    for (int colIndex = 0; colIndex < columnCount; colIndex++)
                    {
                        rowValues[colIndex] = columns[colIndex][rowIndex];
                    }

                    // The nameIndex is shared across all rows in this row group — safe because
                    // ExpressionEvaluator only reads from it and never mutates the row.
                    Row row = new(columnNames, rowValues, nameIndex);

                    if (statement.Where is not null && !evaluator.EvaluateAsBoolean(statement.Where, row))
                    {
                        continue;
                    }

                    for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                    {
                        DataValue evaluated = evaluator.Evaluate(statement.Assignments[assignIndex].Value, row);
                        DatumColumnDescriptor col = schema.Columns[assignmentColumnIndices[assignIndex]];
                        updatedColumns[assignIndex][rowIndex] = CoerceDataValue(evaluated, col.Kind);
                    }

                    anyChange = true;
                    updatedRows++;
                }

                if (anyChange)
                {
                    for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                    {
                        int columnIndex = assignmentColumnIndices[assignIndex];
                        DatumColumnDescriptor columnDescriptor = schema.Columns[columnIndex];
                        DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(columnDescriptor);
                        DatumEncodedPage page = encoder.Encode(updatedColumns[assignIndex], columnDescriptor);
                        replacements.Add(new ColumnPageReplacement(columnIndex, rowGroupIndex, page));
                    }
                }
            }
        }
        finally
        {
            localBufferPool.Dispose();
        }

        if (replacements.Count > 0)
        {
            using FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite);
            DatumFileEditor.ReplaceColumns(stream, replacements);
            _queryContext.Catalog.InvalidateAnalysis(tableName);
        }

        return CommandResult.AffectedRows(updatedRows, $"Updated {updatedRows} rows in '{tableName}'.");
    }

    /// <summary>
    /// Executes an UPDATE...FROM statement by synthesising a join query from the target and
    /// all FROM/JOIN sources, evaluating SET expressions against each joined row, then writing
    /// in-place column page replacements for matching target rows.
    /// </summary>
    /// <remarks>
    /// Follows PostgreSQL semantics: the target table is not repeated in the FROM clause; the
    /// optimizer sees a cross product of target × source filtered by the WHERE predicate. When
    /// multiple source rows match the same target row, the last match wins (indeterminate order).
    /// Target row identity is a <see cref="CompositeKey"/> over all target column values.
    /// Rows with identical values in every column cannot be individually distinguished — in
    /// practice this is only a concern for tables without a primary key.
    /// </remarks>
    private async Task<CommandResult> ExecuteFromUpdateAsync(
        UpdateStatement statement,
        TableDescriptor descriptor,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        _ = parameters;

        string tableName = statement.TableName;

        // The alias is how the query engine qualifies target columns in the join result rows.
        // If no AS alias was given, fall back to the table name itself.
        string effectiveAlias = statement.Alias ?? tableName;

        DatumFileSchema targetSchema;
        DatumRowGroupDescriptor[] rowGroups;

        using (DatumFileReader schemaReader = DatumFileReader.Open(descriptor.FilePath))
        {
            targetSchema = schemaReader.FileSchema;
            using FileStream footerStream = File.OpenRead(descriptor.FilePath);
            (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
        }

        int assignmentCount = statement.Assignments.Count;
        int[] assignmentColumnIndices = new int[assignmentCount];
        for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
        {
            assignmentColumnIndices[assignIndex] =
                FindColumnIndex(targetSchema, statement.Assignments[assignIndex].ColumnName);
        }

        // Synthesise: SELECT * FROM <target> [AS alias] INNER JOIN <from_source> [JOIN ...] WHERE <where>
        // The FROM source becomes an implicit inner join with no explicit ON clause — the WHERE
        // provides the equi-join conditions (PostgreSQL UPDATE...FROM semantics).
        List<JoinClause> syntheticJoins = new() { new JoinClause(JoinType.Inner, statement.From!.Source, null) };
        if (statement.Joins is not null)
        {
            syntheticJoins.AddRange(statement.Joins);
        }

        SelectStatement joinSelect = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference(tableName, effectiveAlias)),
            Joins: syntheticJoins,
            Where: statement.Where);

        QueryExpression joinQuery = new SelectQueryExpression(joinSelect);

        // Execute the join and build: target-row fingerprint → evaluated SET assignment values.
        ExpressionEvaluator evaluator = new(_session.FunctionRegistry);
        Dictionary<CompositeKey, DataValue[]> assignmentMap = new();

        QueryPlanner planner = new(_queryContext.Catalog, _session.FunctionRegistry, _session.VirtualSchemaRegistry);
        LocalBufferPool localBufferPool = GlobalPool.RentLocalBufferPool();

        try
        {
            ExecutionContext context = new(
                cancellationToken,
                _session.FunctionRegistry,
                _queryContext.Catalog,
                localBufferPool,
                queryMeter,
                memoryBudgetBytes: _session.Governor.MemoryBudgetBytes,
                store: new Arena())
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
                MaxStratifyClasses = _session.Governor.MaxStratifyClasses,
            };

            IQueryOperator plan = await planner
                .PlanWithSubqueriesAsync(joinQuery, context, cancellationToken)
                .ConfigureAwait(false);

            await foreach (RowBatch batch in plan.ExecuteAsync(context)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
                {
                    Row joinRow = batch[rowIndex];

                    // Extract the target-side column values to form the identity fingerprint.
                    // AliasOperator qualifies target columns as "<effectiveAlias>.<column>".
                    DataValue[] fingerprintValues = new DataValue[targetSchema.ColumnCount];
                    for (int colIndex = 0; colIndex < targetSchema.ColumnCount; colIndex++)
                    {
                        string qualifiedName = $"{effectiveAlias}.{targetSchema.Columns[colIndex].Name}";
                        fingerprintValues[colIndex] = joinRow.TryGetValue(qualifiedName, out DataValue val)
                            ? val
                            : DataValue.Null(targetSchema.Columns[colIndex].Kind);
                    }

                    CompositeKey fingerprint = new(fingerprintValues);

                    // Evaluate each SET expression against the full joined row so that
                    // expressions can reference columns from both the target and source.
                    DataValue[] newValues = new DataValue[assignmentCount];
                    for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                    {
                        DataValue evaluated = evaluator.Evaluate(
                            statement.Assignments[assignIndex].Value, joinRow);
                        DatumColumnDescriptor col = targetSchema.Columns[assignmentColumnIndices[assignIndex]];
                        newValues[assignIndex] = CoerceDataValue(evaluated, col.Kind);
                    }

                    // Last match wins when multiple source rows match the same target row.
                    assignmentMap[fingerprint] = newValues;
                }
            }
        }
        finally
        {
            localBufferPool.Dispose();
        }

        if (assignmentMap.Count == 0)
        {
            return CommandResult.AffectedRows(0, $"Updated 0 rows in '{tableName}'.");
        }

        // Re-read the target file and apply updates for rows whose fingerprint is in the map.
        int targetColumnCount = targetSchema.ColumnCount;
        int[] allColumnIndices = new int[targetColumnCount];
        for (int index = 0; index < targetColumnCount; index++) allColumnIndices[index] = index;

        List<ColumnPageReplacement> replacements = new();
        long updatedRows = 0;

        using (DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath))
        {
            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                if (rowGroup.ActiveRowCount == 0) continue;

                int rowCount = (int)rowGroup.RowCount;
                DataValue[][] columns = reader.ReadColumns(rowGroupIndex, allColumnIndices);

                DataValue[][] updatedColumns = new DataValue[assignmentCount][];
                for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                {
                    updatedColumns[assignIndex] = new DataValue[rowCount];
                    Array.Copy(columns[assignmentColumnIndices[assignIndex]], updatedColumns[assignIndex], rowCount);
                }

                bool anyChange = false;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (rowGroup.IsRowDeleted(rowIndex)) continue;

                    DataValue[] fingerprintValues = new DataValue[targetColumnCount];
                    for (int colIndex = 0; colIndex < targetColumnCount; colIndex++)
                    {
                        fingerprintValues[colIndex] = columns[colIndex][rowIndex];
                    }

                    CompositeKey key = new(fingerprintValues);
                    if (!assignmentMap.TryGetValue(key, out DataValue[]? newValues))
                    {
                        continue;
                    }

                    for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                    {
                        updatedColumns[assignIndex][rowIndex] = newValues[assignIndex];
                    }

                    anyChange = true;
                    updatedRows++;
                }

                if (anyChange)
                {
                    for (int assignIndex = 0; assignIndex < assignmentCount; assignIndex++)
                    {
                        int columnIndex = assignmentColumnIndices[assignIndex];
                        DatumColumnDescriptor columnDescriptor = targetSchema.Columns[columnIndex];
                        DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(columnDescriptor);
                        DatumEncodedPage page = encoder.Encode(updatedColumns[assignIndex], columnDescriptor);
                        replacements.Add(new ColumnPageReplacement(columnIndex, rowGroupIndex, page));
                    }
                }
            }
        }

        if (replacements.Count > 0)
        {
            using FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite);
            DatumFileEditor.ReplaceColumns(stream, replacements);
            _queryContext.Catalog.InvalidateAnalysis(tableName);
        }

        return CommandResult.AffectedRows(updatedRows, $"Updated {updatedRows} rows in '{tableName}'.");
    }

    // ──────────────────── DELETE ────────────────────

    private async Task<CommandResult> ExecuteDeleteAsync(
        DeleteStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        _ = queryMeter;
        _ = parameters;

        string tableName = statement.TableName;

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        DatumFileSchema schema;
        DatumRowGroupDescriptor[] rowGroups;

        using (DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath))
        {
            schema = reader.FileSchema;
            using FileStream footerStream = File.OpenRead(descriptor.FilePath);
            (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
        }

        if (statement.Where is null)
        {
            // DELETE all rows: mark every row in every row group as deleted.
            List<(int RowGroupIndex, byte[] TombstoneBitmap)> tombstoneUpdates = new(rowGroups.Length);
            long totalDeleted = 0;

            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                if (rowGroup.ActiveRowCount == 0) continue;

                int bitmapLength = ((int)rowGroup.RowCount + 7) / 8;
                byte[] bitmap = rowGroup.TombstoneBitmap is not null
                    ? (byte[])rowGroup.TombstoneBitmap.Clone()
                    : new byte[bitmapLength];

                // Set all valid row bits.
                Array.Fill(bitmap, (byte)0xFF);

                tombstoneUpdates.Add((rowGroupIndex, bitmap));
                totalDeleted += rowGroup.ActiveRowCount;
            }

            if (tombstoneUpdates.Count > 0)
            {
                using FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite);
                DatumFileEditor.MarkDeleted(stream, tombstoneUpdates);
                _queryContext.Catalog.InvalidateAnalysis(tableName);
            }

            return CommandResult.AffectedRows(totalDeleted, $"Deleted {totalDeleted} rows from '{tableName}'.");
        }

        // Conditional DELETE: evaluate the WHERE predicate per row per row group.
        long deletedRows = await ExecuteConditionalDeleteAsync(
            descriptor.FilePath, schema, rowGroups, statement.Where, cancellationToken).ConfigureAwait(false);

        if (deletedRows > 0)
        {
            _queryContext.Catalog.InvalidateAnalysis(tableName);
        }

        return CommandResult.AffectedRows(deletedRows, $"Deleted {deletedRows} rows from '{tableName}'.");
    }

    private Task<long> ExecuteConditionalDeleteAsync(
        string filePath,
        DatumFileSchema schema,
        DatumRowGroupDescriptor[] rowGroups,
        Expression whereExpression,
        CancellationToken cancellationToken)
    {
        // Read the file and evaluate the WHERE predicate against each row.
        // Build tombstone bitmaps for row groups that have matching rows.
        List<(int RowGroupIndex, byte[] TombstoneBitmap)> tombstoneUpdates = new();
        long deletedRows = 0;

        // Scope the reader so the file is closed before MarkDeleted reopens it.
        using (DatumFileReader reader = DatumFileReader.Open(filePath))
        {
            Schema querySchema = reader.Schema;

            // Build all column indices for predicate evaluation.
            int[] allIndices = new int[querySchema.Columns.Count];
            for (int columnIndex = 0; columnIndex < allIndices.Length; columnIndex++)
            {
                allIndices[columnIndex] = columnIndex;
            }

            string[] columnNames = Array.ConvertAll(allIndices, i => querySchema.Columns[i].Name);
            Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
            for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
            {
                nameIndex[columnNames[columnIndex]] = columnIndex;
            }

            LocalBufferPool localBufferPool = GlobalPool.RentLocalBufferPool();

            try
            {
                ExpressionEvaluator evaluator = new(_session.FunctionRegistry);

                for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                    if (rowGroup.ActiveRowCount == 0) continue;

                    int rowCount = (int)rowGroup.RowCount;
                    DataValue[][] columns = reader.ReadColumns(rowGroupIndex, allIndices);

                    int bitmapLength = (rowCount + 7) / 8;
                    byte[] bitmap = rowGroup.TombstoneBitmap is not null
                        ? (byte[])rowGroup.TombstoneBitmap.Clone()
                        : new byte[bitmapLength];

                    bool anyNewDeletions = false;

                    for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                    {
                        if (rowGroup.IsRowDeleted(rowIndex)) continue;

                        DataValue[] values = new DataValue[allIndices.Length];
                        for (int colPos = 0; colPos < allIndices.Length; colPos++)
                        {
                            values[colPos] = columns[colPos][rowIndex];
                        }

                        Row row = new(columnNames, values, nameIndex);

                        if (evaluator.EvaluateAsBoolean(whereExpression, row))
                        {
                            int byteIndex = rowIndex >> 3;
                            int bitIndex = rowIndex & 7;
                            bitmap[byteIndex] |= (byte)(1 << bitIndex);
                            deletedRows++;
                            anyNewDeletions = true;
                        }
                    }

                    if (anyNewDeletions)
                    {
                        tombstoneUpdates.Add((rowGroupIndex, bitmap));
                    }
                }
            }
            finally
            {
                localBufferPool.Dispose();
            }
        }

        if (tombstoneUpdates.Count > 0)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite);
            DatumFileEditor.MarkDeleted(stream, tombstoneUpdates);
        }

        return Task.FromResult(deletedRows);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ────────────────────

    private CommandResult ExecuteAlterTableAddColumn(AlterTableAddColumnStatement statement)
    {
        string tableName = statement.TableName;

        if (statement.DefaultValue is not null && statement.ComputedExpression is not null)
        {
            return CommandResult.Error("DEFAULT and AS (computed) are mutually exclusive on ALTER TABLE ADD COLUMN.");
        }

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        DataKind kind = ResolveTypeName(statement.TypeName);
        DatumColumnFlags flags = statement.Nullable ? DatumColumnFlags.Nullable : DatumColumnFlags.None;
        DatumColumnDescriptor newColumn = new(statement.ColumnName, kind, flags);

        if (statement.ComputedExpression is not null)
        {
            return ExecuteAlterTableAddComputedColumn(statement, descriptor, newColumn, kind);
        }

        DatumRowGroupDescriptor[] rowGroups;
        using (FileStream footerStream = File.OpenRead(descriptor.FilePath))
        {
            (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
        }

        // Build one null/default page per row group.
        DatumEncodedPage[] pages = new DatumEncodedPage[rowGroups.Length];
        DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(newColumn);

        for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
        {
            int rowCount = (int)rowGroups[rowGroupIndex].RowCount;
            List<DataValue> values = new(rowCount);
            DataValue fillValue = statement.DefaultValue is not null
                ? EvaluateConstantExpression(statement.DefaultValue, kind)
                : DataValue.Null(kind);

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                values.Add(fillValue);
            }

            pages[rowGroupIndex] = encoder.Encode(values, newColumn);
        }

        using (FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AddColumn(stream, newColumn, pages);
        }

        _queryContext.Catalog.InvalidateAnalysis(tableName);
        return CommandResult.AffectedRows(0, $"Added column '{statement.ColumnName}' to '{tableName}'.");
    }

    /// <summary>
    /// Evaluates a computed expression against every existing row and appends the
    /// resulting column to the file. Mirrors the row-reading pattern from
    /// <see cref="ExecuteConditionalDeleteAsync"/> but writes new column pages
    /// instead of tombstone bitmaps.
    /// </summary>
    private CommandResult ExecuteAlterTableAddComputedColumn(
        AlterTableAddColumnStatement statement,
        TableDescriptor descriptor,
        DatumColumnDescriptor newColumn,
        DataKind targetKind)
    {
        Expression computedExpression = statement.ComputedExpression!;

        string? disallowed = FindDisallowedComputedColumnExpression(computedExpression);
        if (disallowed is not null)
        {
            return CommandResult.Error(
                $"{disallowed} expressions are not allowed in computed column definitions.");
        }
        DatumEncodedPage[] pages;

        using (DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath))
        {
            Schema querySchema = reader.Schema;

            int[] allIndices = new int[querySchema.Columns.Count];
            for (int columnIndex = 0; columnIndex < allIndices.Length; columnIndex++)
            {
                allIndices[columnIndex] = columnIndex;
            }

            string[] columnNames = Array.ConvertAll(allIndices, index => querySchema.Columns[index].Name);
            Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
            for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
            {
                nameIndex[columnNames[columnIndex]] = columnIndex;
            }

            DatumRowGroupDescriptor[] rowGroups;
            using (FileStream footerStream = File.OpenRead(descriptor.FilePath))
            {
                (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
            }

            pages = new DatumEncodedPage[rowGroups.Length];
            DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(newColumn);
            ExpressionEvaluator evaluator = new(_session.FunctionRegistry);

            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                int rowCount = (int)rowGroups[rowGroupIndex].RowCount;
                DataValue[][] columns = reader.ReadColumns(rowGroupIndex, allIndices);
                List<DataValue> values = new(rowCount);

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    DataValue[] rowValues = new DataValue[allIndices.Length];
                    for (int columnPosition = 0; columnPosition < allIndices.Length; columnPosition++)
                    {
                        rowValues[columnPosition] = columns[columnPosition][rowIndex];
                    }

                    Row row = new(columnNames, rowValues, nameIndex);
                    DataValue result = evaluator.Evaluate(computedExpression, row);
                    values.Add(CoerceDataValue(result, targetKind));
                }

                pages[rowGroupIndex] = encoder.Encode(values, newColumn);
            }
        }

        using (FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite))
        {
            DatumFileEditor.AddColumn(stream, newColumn, pages);
        }

        _queryContext.Catalog.InvalidateAnalysis(descriptor.Name);
        return CommandResult.AffectedRows(0, $"Added computed column '{statement.ColumnName}' to '{descriptor.Name}'.");
    }

    // ──────────────────── ANALYZE ────────────────────

    /// <summary>
    /// Rebuilds the source index and column statistics manifest for the specified table,
    /// registering both on the session catalog for use by the query planner.
    /// </summary>
    private CommandResult ExecuteAnalyzeTable(AnalyzeTableStatement statement)
    {
        string tableName = statement.TableName;

        if (!_queryContext.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        if (descriptor.Mutability == TableMutability.SessionOwned
            && !_queryContext.Catalog.IsAnalysisPending(tableName))
        {
            return CommandResult.AffectedRows(0, $"Table '{tableName}' is already up to date.");
        }

        RebuildTempTableSidecars(tableName, descriptor.FilePath);

        return CommandResult.AffectedRows(0, $"Analyzed table '{tableName}'.");
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Checks whether the resolved table permits write operations.
    /// Returns a non-null error result when the table is read-only.
    /// </summary>
    private static CommandResult? CheckMutability(TableDescriptor descriptor)
    {
        if (descriptor.Mutability == TableMutability.ReadOnly)
        {
            return CommandResult.Error(
                $"Table '{descriptor.Name}' is read-only. DDL/DML operations are only permitted on session-owned or writable tables.");
        }

        return null;
    }

    /// <summary>
    /// Resolves a SQL type name (case-insensitive) to a <see cref="DataKind"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the type name is not recognized.</exception>
    internal static DataKind ResolveTypeName(string typeName)
    {
        return typeName.ToUpperInvariant() switch
        {
            "INT" or "INT32" or "INTEGER" => DataKind.Int32,
            "BIGINT" or "INT64" => DataKind.Int64,
            "SMALLINT" or "INT16" => DataKind.Int16,
            "TINYINT" or "INT8" => DataKind.Int8,
            "UINT8" => DataKind.UInt8,
            "UINT16" => DataKind.UInt16,
            "UINT32" => DataKind.UInt32,
            "UINT64" => DataKind.UInt64,
            "FLOAT" or "FLOAT32" or "REAL" => DataKind.Float32,
            "DOUBLE" or "FLOAT64" => DataKind.Float64,
            "STRING" or "TEXT" or "VARCHAR" => DataKind.String,
            "BOOL" or "BOOLEAN" => DataKind.Boolean,
            "DATE" => DataKind.Date,
            "DATETIME" or "TIMESTAMP" => DataKind.DateTime,
            "TIME" => DataKind.Time,
            "DURATION" or "INTERVAL" => DataKind.Duration,
            "UUID" => DataKind.Uuid,
            "JSON" or "JSONVALUE" => DataKind.JsonValue,
            "BLOB" or "BINARY" or "UINT8ARRAY" => DataKind.UInt8Array,
            "IMAGE" => DataKind.Image,
            _ => throw new InvalidOperationException($"Unknown SQL type name '{typeName}'."),
        };
    }

    private string GetTempFilePath(string tableName)
    {
        return _queryContext.GetTempFilePath(tableName);
    }

    private void RegisterTempTable(string tableName, string filePath, IReadOnlyList<string>? primaryKeyColumns = null)
    {
        TableDescriptor descriptor = new("datum", tableName, filePath, new Dictionary<string, string>(),
            Mutability: TableMutability.SessionOwned,
            PrimaryKeyColumns: primaryKeyColumns);
        _queryContext.Catalog.Register(descriptor);
    }

    private async Task<long> MaterializeQueryToFileAsync(
        QueryExpression query,
        string filePath,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        if (parameters is not null && parameters.Count > 0)
        {
            query = ParameterBinder.Bind(query, parameters);
        }

        QueryPlanner planner = new(_queryContext.Catalog, _session.FunctionRegistry, _session.VirtualSchemaRegistry);
        LocalBufferPool localBufferPool = GlobalPool.RentLocalBufferPool();

        try
        {
            ExecutionContext context = new(cancellationToken, _session.FunctionRegistry, _queryContext.Catalog, localBufferPool, queryMeter,
                memoryBudgetBytes: _session.Governor.MemoryBudgetBytes, store: new Arena())
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
                MaxStratifyClasses = _session.Governor.MaxStratifyClasses,
            };

            IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, cancellationToken).ConfigureAwait(false);

            await using DatumOutputWriter writer = new(filePath);
            Schema? schema = null;
            long rowCount = 0;

            await foreach (RowBatch batch in plan.ExecuteAsync(context).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (schema is null && batch.Count > 0)
                {
                    Row firstRow = batch[0];
                    IReadOnlyList<string> columnNames = firstRow.ColumnNames;
                    List<ColumnInfo> typedColumns = new(columnNames.Count);
                    for (int columnIndex = 0; columnIndex < columnNames.Count; columnIndex++)
                    {
                        typedColumns.Add(new ColumnInfo(
                            columnNames[columnIndex],
                            firstRow[columnIndex].Kind,
                            nullable: true));
                    }

                    schema = new Schema(typedColumns);
                    await writer.InitializeAsync(schema, cancellationToken).ConfigureAwait(false);
                }

                for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
                {
                    Row row = batch[rowIndex];
                    await writer.WriteRowAsync(row, cancellationToken).ConfigureAwait(false);
                    rowCount++;
                }
            }

            if (schema is null)
            {
                // No rows returned; create an empty file with a dummy schema.
                schema = new Schema([new ColumnInfo("_empty", DataKind.Int32, nullable: true)]);
                await writer.InitializeAsync(schema, cancellationToken).ConfigureAwait(false);
            }

            await writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
            return rowCount;
        }
        finally
        {
            localBufferPool.Dispose();
        }
    }

    private static void AppendBufferedRows(
        string filePath,
        DatumFileSchema schema,
        List<List<DataValue>> columnBuffers,
        uint rowCount)
    {
        DatumEncodedPage[] pages = new DatumEncodedPage[schema.ColumnCount];

        for (int columnIndex = 0; columnIndex < schema.ColumnCount; columnIndex++)
        {
            DatumColumnDescriptor descriptor = schema.Columns[columnIndex];
            DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(descriptor);
            pages[columnIndex] = encoder.Encode(columnBuffers[columnIndex], descriptor);
        }

        RowGroupPayload payload = new(rowCount, pages);

        using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite);
        DatumFileEditor.AppendRowGroups(stream, [payload]);
    }

    /// <summary>
    /// Validates that no non-nullable column contains a NULL value in the new rows.
    /// Returns <see langword="null"/> if validation passes, or an error message
    /// identifying the first violating column.
    /// </summary>
    private static string? ValidateNotNullConstraints(
        DatumFileSchema schema,
        List<List<DataValue>> columnBuffers)
    {
        for (int columnIndex = 0; columnIndex < schema.ColumnCount; columnIndex++)
        {
            if (schema.Columns[columnIndex].IsNullable)
            {
                continue;
            }

            List<DataValue> buffer = columnBuffers[columnIndex];
            for (int rowIndex = 0; rowIndex < buffer.Count; rowIndex++)
            {
                if (buffer[rowIndex].IsNull)
                {
                    return $"NOT NULL constraint violation: column '{schema.Columns[columnIndex].Name}' " +
                           $"does not allow NULL values (row {rowIndex + 1}).";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that the new rows being inserted do not violate the primary key
    /// uniqueness constraint. Reads existing PK column values from the file, builds
    /// a hash set, then checks each new row against it and against other new rows.
    /// Returns <see langword="null"/> if validation passes, or an error message if a
    /// duplicate is detected.
    /// </summary>
    private static string? ValidatePrimaryKeyUniqueness(
        string filePath,
        DatumFileSchema schema,
        List<List<DataValue>> newColumnBuffers,
        IReadOnlyList<string> primaryKeyColumns)
    {
        // Resolve PK column indices in the schema.
        int[] primaryKeyIndices = new int[primaryKeyColumns.Count];
        for (int keyIndex = 0; keyIndex < primaryKeyColumns.Count; keyIndex++)
        {
            primaryKeyIndices[keyIndex] = FindColumnIndex(schema, primaryKeyColumns[keyIndex]);
        }

        // Build a hash set from existing rows in the file.
        HashSet<CompositeKey> existingKeys = new();

        using (DatumFileReader reader = DatumFileReader.Open(filePath))
        {
            DatumRowGroupDescriptor[] rowGroups;
            using (FileStream footerStream = File.OpenRead(filePath))
            {
                (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
            }

            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                DatumRowGroupDescriptor rowGroup = rowGroups[rowGroupIndex];
                if (rowGroup.ActiveRowCount == 0) continue;

                DataValue[][] columns = reader.ReadColumns(rowGroupIndex, primaryKeyIndices);
                int rowCount = (int)rowGroup.RowCount;

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    if (rowGroup.IsRowDeleted(rowIndex)) continue;

                    DataValue[] keyValues = new DataValue[primaryKeyIndices.Length];
                    for (int keyPosition = 0; keyPosition < primaryKeyIndices.Length; keyPosition++)
                    {
                        keyValues[keyPosition] = columns[keyPosition][rowIndex];
                    }

                    existingKeys.Add(new CompositeKey(keyValues));
                }
            }
        }

        // Check each new row against existing keys and other new rows.
        int newRowCount = newColumnBuffers[0].Count;
        for (int rowIndex = 0; rowIndex < newRowCount; rowIndex++)
        {
            DataValue[] keyValues = new DataValue[primaryKeyIndices.Length];
            for (int keyPosition = 0; keyPosition < primaryKeyIndices.Length; keyPosition++)
            {
                keyValues[keyPosition] = newColumnBuffers[primaryKeyIndices[keyPosition]][rowIndex];
            }

            CompositeKey key = new(keyValues);
            if (!existingKeys.Add(key))
            {
                return $"PRIMARY KEY violation: duplicate key ({key}) in table.";
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a mapping from schema column indices to source row ordinal indices.
    /// Returns -1 for schema columns that have no corresponding source column (filled with null).
    /// </summary>
    private static int[] BuildColumnMapping(
        DatumFileSchema schema,
        IReadOnlyList<string>? targetColumns,
        IQueryOperator plan)
    {
        int[] mapping = new int[schema.ColumnCount];
        Array.Fill(mapping, -1);

        if (targetColumns is null)
        {
            // Positional mapping: source column i → schema column i.
            for (int index = 0; index < Math.Min(schema.ColumnCount, schema.ColumnCount); index++)
            {
                mapping[index] = index;
            }
        }
        else
        {
            for (int targetIndex = 0; targetIndex < targetColumns.Count; targetIndex++)
            {
                int schemaIndex = FindColumnIndex(schema, targetColumns[targetIndex]);
                mapping[schemaIndex] = targetIndex;
            }
        }

        return mapping;
    }

    private static int[] BuildColumnMappingForValues(
        DatumFileSchema schema,
        IReadOnlyList<string>? targetColumns,
        int valueCount)
    {
        int[] mapping = new int[schema.ColumnCount];
        Array.Fill(mapping, -1);

        if (targetColumns is null)
        {
            for (int index = 0; index < Math.Min(schema.ColumnCount, valueCount); index++)
            {
                mapping[index] = index;
            }
        }
        else
        {
            for (int targetIndex = 0; targetIndex < targetColumns.Count; targetIndex++)
            {
                int schemaIndex = FindColumnIndex(schema, targetColumns[targetIndex]);
                mapping[schemaIndex] = targetIndex;
            }
        }

        return mapping;
    }

    private static int FindColumnIndex(DatumFileSchema schema, string columnName)
    {
        for (int index = 0; index < schema.ColumnCount; index++)
        {
            if (string.Equals(schema.Columns[index].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new KeyNotFoundException($"Column '{columnName}' not found in schema.");
    }

    /// <summary>
    /// Evaluates a constant expression (literal) to a <see cref="DataValue"/>.
    /// Only supports literals and NULL for now; full expression evaluation
    /// will be wired in a later phase.
    /// </summary>
    private static DataValue EvaluateConstantExpression(Expression expression, DataKind targetKind)
    {
        return expression switch
        {
            LiteralExpression { Value: null } => DataValue.Null(targetKind),
            LiteralExpression { Value: { } value } => CoerceLiteral(value, targetKind),
            UnaryExpression { Operator: UnaryOperator.Negate, Operand: LiteralExpression { Value: { } innerValue } } =>
                NegateNumericLiteral(innerValue, targetKind),
            _ => throw new InvalidOperationException(
                $"Only constant expressions are supported in this context. Got: {expression.GetType().Name}."),
        };
    }

    private static DataValue CoerceLiteral(object value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(value)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(value)),
            DataKind.Int16 => DataValue.FromInt16(Convert.ToInt16(value)),
            DataKind.Int8 => DataValue.FromInt8(Convert.ToSByte(value)),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(value)),
            DataKind.UInt16 => DataValue.FromUInt16(Convert.ToUInt16(value)),
            DataKind.UInt32 => DataValue.FromUInt32(Convert.ToUInt32(value)),
            DataKind.UInt64 => DataValue.FromUInt64(Convert.ToUInt64(value)),
            DataKind.Float32 => DataValue.FromFloat32(Convert.ToSingle(value)),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(value)),
            DataKind.String => DataValue.FromString(Convert.ToString(value) ?? string.Empty),
            DataKind.Boolean => DataValue.FromBoolean(Convert.ToBoolean(value)),
            _ => throw new InvalidOperationException(
                $"Cannot coerce literal of type {value.GetType().Name} to {targetKind}."),
        };
    }

    private static DataValue NegateNumericLiteral(object value, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Int32 => DataValue.FromInt32(-Convert.ToInt32(value)),
            DataKind.Int64 => DataValue.FromInt64(-Convert.ToInt64(value)),
            DataKind.Int16 => DataValue.FromInt16((short)-Convert.ToInt16(value)),
            DataKind.Int8 => DataValue.FromInt8((sbyte)-Convert.ToSByte(value)),
            DataKind.Float32 => DataValue.FromFloat32(-Convert.ToSingle(value)),
            DataKind.Float64 => DataValue.FromFloat64(-Convert.ToDouble(value)),
            _ => throw new InvalidOperationException(
                $"Cannot negate literal for target type {targetKind}."),
        };
    }

    /// <summary>
    /// Coerces a <see cref="DataValue"/> produced by the expression evaluator to the
    /// declared column type. Null values are preserved with the target kind.
    /// Numeric values are widened or narrowed through <see cref="double"/> conversion.
    /// </summary>
    private static DataValue CoerceDataValue(DataValue value, DataKind targetKind)
    {
        if (value.IsNull)
        {
            return DataValue.Null(targetKind);
        }

        if (value.Kind == targetKind)
        {
            return value;
        }

        // Extract the numeric value as double for safe widening/narrowing.
        if (!value.TryToDouble(out double numeric))
        {
            throw new InvalidOperationException(
                $"Cannot coerce {value.Kind} to {targetKind} in computed column expression.");
        }

        return targetKind switch
        {
            DataKind.Float32 => DataValue.FromFloat32((float)numeric),
            DataKind.Float64 => DataValue.FromFloat64(numeric),
            DataKind.Int8 => DataValue.FromInt8((sbyte)numeric),
            DataKind.Int16 => DataValue.FromInt16((short)numeric),
            DataKind.Int32 => DataValue.FromInt32((int)numeric),
            DataKind.Int64 => DataValue.FromInt64((long)numeric),
            DataKind.UInt8 => DataValue.FromUInt8((byte)numeric),
            DataKind.UInt16 => DataValue.FromUInt16((ushort)numeric),
            DataKind.UInt32 => DataValue.FromUInt32((uint)numeric),
            DataKind.UInt64 => DataValue.FromUInt64((ulong)numeric),
            DataKind.Boolean => DataValue.FromBoolean(numeric != 0.0),
            _ => throw new InvalidOperationException(
                $"Cannot coerce {value.Kind} to {targetKind} in computed column expression."),
        };
    }

    /// <summary>
    /// Recursively walks an expression tree and returns the name of the first
    /// disallowed node type found, or <c>null</c> if the expression is valid
    /// for use in a computed column definition. Subquery and window function
    /// expressions are rejected because computed columns are evaluated row-by-row
    /// without access to the query planner.
    /// </summary>
    private static string? FindDisallowedComputedColumnExpression(Expression expression)
    {
        switch (expression)
        {
            case SubqueryExpression:
                return "Subquery";
            case InSubqueryExpression:
                return "IN (subquery)";
            case ExistsExpression:
                return "EXISTS";
            case WindowFunctionCallExpression:
                return "Window function";

            case BinaryExpression binary:
                return FindDisallowedComputedColumnExpression(binary.Left)
                    ?? FindDisallowedComputedColumnExpression(binary.Right);

            case UnaryExpression unary:
                return FindDisallowedComputedColumnExpression(unary.Operand);

            case FunctionCallExpression function:
                foreach (Expression argument in function.Arguments)
                {
                    string? result = FindDisallowedComputedColumnExpression(argument);
                    if (result is not null) return result;
                }
                return null;

            case LikeExpression like:
                return FindDisallowedComputedColumnExpression(like.Expression)
                    ?? FindDisallowedComputedColumnExpression(like.Pattern)
                    ?? FindDisallowedComputedColumnExpression(like.EscapeCharacter);

            case InExpression inExpression:
                string? inResult = FindDisallowedComputedColumnExpression(inExpression.Expression);
                if (inResult is not null) return inResult;
                foreach (Expression value in inExpression.Values)
                {
                    inResult = FindDisallowedComputedColumnExpression(value);
                    if (inResult is not null) return inResult;
                }
                return null;

            case BetweenExpression between:
                return FindDisallowedComputedColumnExpression(between.Expression)
                    ?? FindDisallowedComputedColumnExpression(between.Low)
                    ?? FindDisallowedComputedColumnExpression(between.High);

            case IsNullExpression isNull:
                return FindDisallowedComputedColumnExpression(isNull.Expression);

            case CastExpression cast:
                return FindDisallowedComputedColumnExpression(cast.Expression);

            case CaseExpression caseExpression:
                if (caseExpression.Operand is not null)
                {
                    string? operandResult = FindDisallowedComputedColumnExpression(caseExpression.Operand);
                    if (operandResult is not null) return operandResult;
                }
                foreach (WhenClause whenClause in caseExpression.WhenClauses)
                {
                    string? whenResult = FindDisallowedComputedColumnExpression(whenClause.Condition)
                        ?? FindDisallowedComputedColumnExpression(whenClause.Result);
                    if (whenResult is not null) return whenResult;
                }
                if (caseExpression.ElseResult is not null)
                {
                    return FindDisallowedComputedColumnExpression(caseExpression.ElseResult);
                }
                return null;

            default:
                return null;
        }
    }
}
