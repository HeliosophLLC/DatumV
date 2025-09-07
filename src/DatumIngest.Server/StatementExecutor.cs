using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Encoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Parsing.Ast;
using DatumIngest.Statistics;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Server;

/// <summary>
/// Executes DDL and DML <see cref="Statement"/> nodes against a session's catalog
/// and temp table storage. Handles CREATE TEMP TABLE, INSERT, UPDATE, DELETE,
/// ALTER ADD, and DROP TABLE operations.
/// </summary>
internal sealed class StatementExecutor
{
    private readonly Session _session;
    private readonly ParallelismBudget? _parallelismBudget;

    /// <summary>
    /// Initializes a new statement executor.
    /// </summary>
    /// <param name="session">The session owning the catalog and temp directory.</param>
    /// <param name="parallelismBudget">Optional global parallelism budget, or <see langword="null"/> for unlimited.</param>
    internal StatementExecutor(Session session, ParallelismBudget? parallelismBudget = null)
    {
        _session = session;
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
            _ => throw new InvalidOperationException(
                $"Statement type {statement.GetType().Name} is not executable as DDL/DML."),
        };
    }

    // ──────────────────── CREATE TEMP TABLE ────────────────────

    private CommandResult ExecuteCreateTempTable(CreateTempTableStatement statement)
    {
        string tableName = statement.TableName;

        if (_session.Catalog.TryResolve(tableName, out _))
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

        RegisterTempTable(tableName, filePath);
        return CommandResult.AffectedRows(0, $"Created temp table '{tableName}'.");
    }

    private async Task<CommandResult> ExecuteCreateTempTableAsSelectAsync(
        CreateTempTableAsSelectStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        string tableName = statement.TableName;

        if (_session.Catalog.TryResolve(tableName, out _))
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

        if (!_session.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            if (statement.IfExists)
            {
                return CommandResult.AffectedRows(0, $"Table '{tableName}' does not exist.");
            }

            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        _session.Catalog.Unregister(tableName);

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

        if (!_session.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
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

        long insertedRows;

        switch (statement.Source)
        {
            case InsertQuerySource querySource:
                insertedRows = await InsertFromQueryAsync(
                    descriptor.FilePath, schema, querySource.Query,
                    statement.ColumnNames, cancellationToken, queryMeter, parameters).ConfigureAwait(false);
                break;

            case InsertValuesSource valuesSource:
                insertedRows = InsertFromValues(
                    descriptor.FilePath, schema, valuesSource.Rows, statement.ColumnNames);
                break;

            default:
                return CommandResult.Error($"Unsupported INSERT source: {statement.Source.GetType().Name}.");
        }

        if (insertedRows > 0 && descriptor.Mutability == TableMutability.SessionOwned)
        {
            RebuildTempTableSidecars(tableName, descriptor.FilePath);
        }

        return CommandResult.AffectedRows(insertedRows, $"Inserted {insertedRows} rows into '{tableName}'.");
    }

    /// <summary>
    /// Rebuilds the source index and column statistics manifest for a session-owned table,
    /// registering both on the session catalog for use by the query planner.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IncrementalIndexBuilder"/> to stream rows through the indexing pipeline
    /// with disk-based spill, avoiding full in-memory materialization. The completed index sidecar
    /// is written alongside the <c>.datum</c> file and the manifest is registered on the catalog.
    /// </remarks>
    private void RebuildTempTableSidecars(string tableName, string filePath)
    {
        SourceIndexBuilder sourceIndexBuilder = new(
            bloomAllColumns: false, indexAllColumns: false, autoIndexColumns: true);
        SourceFingerprint fingerprint = new(0, Array.Empty<byte>());
        IncrementalIndexBuilder indexBuilder = sourceIndexBuilder.CreateIncrementalBuilder(fingerprint);
        StatisticsCollector statisticsCollector = new();

        Schema schema;
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
                    indexBuilder.AddRow(row);
                    statisticsCollector.AddRow(row);
                }
            }
        }

        // Finalize the index and write the sidecar.
        SourceIndex sourceIndex = indexBuilder.Finalize();
        SourceIndexSet indexSet = SourceIndexSet.Create(tableName, sourceIndex);

        string indexPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-index";
        using (FileStream output = File.Create(indexPath))
        {
            UnifiedIndexWriter.Write(indexSet, output, indexBuilder.SpillWriter);
        }

        indexBuilder.Dispose();

        _session.Catalog.RegisterIndex(tableName, sourceIndex);

        // Build and register the manifest.
        IReadOnlyDictionary<string, ColumnStatistics> statistics = statisticsCollector.GetStatistics();
        Dictionary<string, DataKind> columnKinds = new(schema.Columns.Count, StringComparer.OrdinalIgnoreCase);
        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            columnKinds[schema.Columns[columnIndex].Name] = schema.Columns[columnIndex].Kind;
        }

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, columnKinds, sourceIndex.Schema.TotalRowCount);

        string manifestPath = FileFormatDetector.GetSidecarBasePath(filePath) + ".datum-manifest";
        ManifestSerializer.WriteToFileAsync(tableName, manifest, manifestPath).GetAwaiter().GetResult();

        _session.Catalog.RegisterManifest(tableName, manifest);
    }

    private async Task<long> InsertFromQueryAsync(
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

        QueryPlanner planner = new(_session.Catalog, _session.FunctionRegistry);
        LocalBufferPool localBufferPool = GlobalBufferPool.RentLocalBufferPool();

        try
        {
            ExecutionContext context = new(cancellationToken, _session.FunctionRegistry, _session.Catalog, localBufferPool, queryMeter,
                memoryBudgetBytes: _session.Governor.MemoryBudgetBytes)
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
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

            if (rowCount > 0)
            {
                AppendBufferedRows(filePath, schema, columnBuffers, (uint)rowCount);
            }

            return rowCount;
        }
        finally
        {
            localBufferPool.Dispose();
        }
    }

    private static long InsertFromValues(
        string filePath,
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

        if (rows.Count > 0)
        {
            AppendBufferedRows(filePath, schema, columnBuffers, (uint)rows.Count);
        }

        return rows.Count;
    }

    // ──────────────────── UPDATE ────────────────────

    private async Task<CommandResult> ExecuteUpdateAsync(
        UpdateStatement statement,
        CancellationToken cancellationToken,
        QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters)
    {
        _ = queryMeter;
        _ = parameters;
        _ = cancellationToken;

        string tableName = statement.TableName;

        if (!_session.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        // For now, UPDATE only supports constant assignments (SET col = literal).
        // Full expression evaluation with WHERE requires the expression evaluator,
        // which will be wired in a later phase.
        DatumFileSchema schema;
        DatumRowGroupDescriptor[] rowGroups;
        long totalRowCount;

        using (DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath))
        {
            schema = reader.FileSchema;
            totalRowCount = reader.TotalRowCount;

            // Read row groups using the footer.
            using FileStream footerStream = File.OpenRead(descriptor.FilePath);
            (_, rowGroups, _, _) = DatumFileReader.ReadFooterAndHeader(footerStream);
        }

        // Build the list of column replacements.
        List<ColumnPageReplacement> replacements = new();

        foreach (ColumnAssignment assignment in statement.Assignments)
        {
            int columnIndex = FindColumnIndex(schema, assignment.ColumnName);
            DatumColumnDescriptor columnDescriptor = schema.Columns[columnIndex];
            DataValue constantValue = EvaluateConstantExpression(assignment.Value, columnDescriptor.Kind);

            for (int rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                int rowCount = (int)rowGroups[rowGroupIndex].RowCount;
                List<DataValue> values = new(rowCount);

                for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    values.Add(constantValue);
                }

                DatumColumnEncoder encoder = DatumEncoderFactory.GetEncoder(columnDescriptor);
                DatumEncodedPage page = encoder.Encode(values, columnDescriptor);
                replacements.Add(new ColumnPageReplacement(columnIndex, rowGroupIndex, page));
            }
        }

        if (replacements.Count > 0)
        {
            using FileStream stream = new(descriptor.FilePath, FileMode.Open, FileAccess.ReadWrite);
            DatumFileEditor.ReplaceColumns(stream, replacements);
        }

        return CommandResult.AffectedRows(totalRowCount, $"Updated {totalRowCount} rows in '{tableName}'.");
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

        if (!_session.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
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
            }

            return CommandResult.AffectedRows(totalDeleted, $"Deleted {totalDeleted} rows from '{tableName}'.");
        }

        // Conditional DELETE: evaluate the WHERE predicate per row per row group.
        long deletedRows = await ExecuteConditionalDeleteAsync(
            descriptor.FilePath, schema, rowGroups, statement.Where, cancellationToken).ConfigureAwait(false);

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

            LocalBufferPool localBufferPool = GlobalBufferPool.RentLocalBufferPool();

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

        if (!_session.Catalog.TryResolve(tableName, out TableDescriptor? descriptor) || descriptor is null)
        {
            return CommandResult.Error($"Table '{tableName}' does not exist.");
        }

        CommandResult? mutabilityError = CheckMutability(descriptor);
        if (mutabilityError is not null) return mutabilityError;

        DataKind kind = ResolveTypeName(statement.TypeName);
        DatumColumnFlags flags = statement.Nullable ? DatumColumnFlags.Nullable : DatumColumnFlags.None;
        DatumColumnDescriptor newColumn = new(statement.ColumnName, kind, flags);

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

        return CommandResult.AffectedRows(0, $"Added column '{statement.ColumnName}' to '{tableName}'.");
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_session_{_session.SessionId:N}");
        Directory.CreateDirectory(tempDirectory);
        return Path.Combine(tempDirectory, $"{tableName}.datum");
    }

    private void RegisterTempTable(string tableName, string filePath)
    {
        TableDescriptor descriptor = new("datum", tableName, filePath, new Dictionary<string, string>(),
            Mutability: TableMutability.SessionOwned);
        _session.Catalog.Register(descriptor);
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

        QueryPlanner planner = new(_session.Catalog, _session.FunctionRegistry);
        LocalBufferPool localBufferPool = GlobalBufferPool.RentLocalBufferPool();

        try
        {
            ExecutionContext context = new(cancellationToken, _session.FunctionRegistry, _session.Catalog, localBufferPool, queryMeter,
                memoryBudgetBytes: _session.Governor.MemoryBudgetBytes)
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
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
}
