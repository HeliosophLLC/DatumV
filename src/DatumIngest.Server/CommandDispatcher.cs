using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Server;

/// <summary>
/// Routes command input through permission checks, parsing, planning,
/// and execution, returning a <see cref="CommandResult"/> for consumption
/// by any frontend (CLI shell, gRPC server, etc.).
/// </summary>
public sealed class CommandDispatcher
{
    private readonly SessionManager _sessionManager;
    private readonly ParallelismBudget? _parallelismBudget;

    /// <summary>
    /// Initializes a new command dispatcher.
    /// </summary>
    /// <param name="sessionManager">Session manager for session-level operations (list, kill).</param>
    /// <param name="parallelismBudget">
    /// Optional global concurrency budget that bounds the total number of parallel
    /// operator workers across all concurrent queries. When <see langword="null"/>,
    /// operators may spawn up to <see cref="ExecutionContext.DegreeOfParallelism"/>
    /// workers without limit — appropriate for single-query CLI usage.
    /// </param>
    public CommandDispatcher(SessionManager sessionManager, ParallelismBudget? parallelismBudget = null)
    {
        _sessionManager = sessionManager;
        _parallelismBudget = parallelismBudget;
    }

    /// <summary>
    /// Dispatches a command string, enforcing permissions and routing to
    /// the appropriate handler.
    /// </summary>
    /// <param name="session">The session issuing the command.</param>
    /// <param name="input">The raw command text (SQL or dot-command).</param>
    /// <param name="cancellationToken">Cancellation token for this operation.</param>
    /// <param name="queryMeter">Optional meter for accumulating Query Unit costs, or <see langword="null"/> for unmetered execution.</param>
    /// <param name="parameters">Optional named parameter bindings to substitute into the query, or <see langword="null"/> for no parameters.</param>
    /// <returns>The result of the command execution.</returns>
    public async Task<CommandResult> DispatchAsync(
        Session session, string input, CancellationToken cancellationToken, QueryMeter? queryMeter = null,
        IReadOnlyDictionary<string, DataValue>? parameters = null)
    {
        session.TouchActivity();

        if (session.DatasetId is not null)
        {
            _sessionManager.TouchDataset(session.DatasetId);
        }

        string trimmed = input.Trim().TrimEnd(';');

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return CommandResult.Success("");
        }

        try
        {
            if (trimmed.StartsWith('.'))
            {
                return await DispatchMetaCommandAsync(session, trimmed, cancellationToken).ConfigureAwait(false);
            }

            return await DispatchSqlAsync(session, trimmed, cancellationToken, queryMeter, parameters).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Error("Query cancelled.");
        }
        catch (ParseException ex)
        {
            return CommandResult.Error($"Syntax error: {ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            return CommandResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Error: {ex.Message}");
        }
    }

    private async Task<CommandResult> DispatchMetaCommandAsync(
        Session session, string input, CancellationToken cancellationToken)
    {
        // Split into command name and argument.
        string[] parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1] : "";

        return command switch
        {
            ".help" => HandleHelp(),
            ".tables" => HandleListTables(session),
            ".schema" or ".columns" => await HandleSchemaAsync(session, argument, cancellationToken).ConfigureAwait(false),
            ".providers" => HandleListProviders(session),
            ".functions" => HandleListFunctions(session),
            ".source" => await HandleAddSourceAsync(session, argument, cancellationToken).ConfigureAwait(false),
            ".explain" => await HandleExplainAsync(session, argument, cancellationToken).ConfigureAwait(false),
            ".sessions" => HandleListSessions(session),
            ".kill" => HandleKillQuery(session, argument),
            ".cancel" => HandleCancelQuery(session, argument),
            _ => CommandResult.Error($"Unknown command: {command}. Type .help for available commands."),
        };
    }

    private async Task<CommandResult> DispatchSqlAsync(
        Session session, string input, CancellationToken cancellationToken, QueryMeter? queryMeter,
        IReadOnlyDictionary<string, DataValue>? parameters = null)
    {
        if (!session.IsAuthorized(ServerOperation.Query))
        {
            return CommandResult.Error("Permission denied: you are not authorized to run queries.");
        }

        session.RecordQuery(input);

        QueryExpression query = SqlParser.Parse(input);

        // Bind named parameters ($name) to concrete literal values before planning.
        if (parameters is not null && parameters.Count > 0)
        {
            query = ParameterBinder.Bind(query, parameters);
        }

        QueryPlanner planner = new(session.Catalog, session.FunctionRegistry);
        LocalBufferPool localBufferPool = GlobalBufferPool.RentLocalBufferPool();
        ExecutionContext context = new(cancellationToken, session.FunctionRegistry, session.Catalog, localBufferPool, queryMeter,
            memoryBudgetBytes: session.Governor.MemoryBudgetBytes)
        {
            DegreeOfParallelism = Environment.ProcessorCount,
            ParallelismBudget = _parallelismBudget,
        };
        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, cancellationToken).ConfigureAwait(false);

        // Wrap the deferred execution so the pool is disposed after the stream
        // is fully consumed (or abandoned). The pool cannot be scoped with `using`
        // here because the IAsyncEnumerable outlives this method.
        IAsyncEnumerable<Row> rows = StreamWithDisposal(plan.ExecuteAsync(context), localBufferPool);

        // We need the schema before streaming. Use the leftmost SELECT for column metadata.
        SelectStatement schemaStatement = ExtractLeftmostStatement(query);
        Schema schema = await ResolveQuerySchemaAsync(session, schemaStatement, cancellationToken).ConfigureAwait(false);

        return CommandResult.StreamingRows(rows, schema);
    }

    private static async Task<Schema> ResolveQuerySchemaAsync(
        Session session, SelectStatement statement, CancellationToken cancellationToken)
    {
        QuerySchemaResolver resolver = new(session.Catalog, session.FunctionRegistry);
        ResolvedQuerySchema resolved = await resolver.ResolveAsync(statement, cancellationToken).ConfigureAwait(false);

        // Build a source schema for type inference on expressions.
        // Use first-occurrence-wins dedup so that columns shared across
        // JOINed tables do not cause duplicate-name exceptions.
        List<ColumnInfo> sourceColumns = new();
        HashSet<string> seenSourceNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedColumn column in resolved.Columns)
        {
            // Add qualified name (table.column) so ExpressionTypeResolver
            // can resolve qualified column references.
            if (column.SourceTableOrAlias is not null)
            {
                string qualifiedName = $"{column.SourceTableOrAlias}.{column.ColumnName}";
                if (seenSourceNames.Add(qualifiedName))
                {
                    sourceColumns.Add(new ColumnInfo(qualifiedName, column.Kind, column.Nullable));
                }
            }

            if (seenSourceNames.Add(column.ColumnName))
            {
                sourceColumns.Add(new ColumnInfo(column.ColumnName, column.Kind, column.Nullable));
            }
        }

        Schema sourceSchema = new(sourceColumns);

        // Apply the SELECT clause projection to produce only the output columns.
        List<ColumnInfo> outputColumns = new();
        HashSet<int> aliasedPositions = new();

        foreach (SelectColumn selectColumn in statement.Columns)
        {
            switch (selectColumn)
            {
                case SelectAllColumns:
                    foreach (ResolvedColumn column in resolved.Columns)
                    {
                        outputColumns.Add(new ColumnInfo(column.ColumnName, column.Kind, column.Nullable));
                    }
                    break;

                case SelectTableColumns tableColumns:
                    foreach (ResolvedColumn column in resolved.FindColumns(tableColumns.TableName))
                    {
                        outputColumns.Add(new ColumnInfo(column.ColumnName, column.Kind, column.Nullable));
                    }
                    break;

                default:
                    string outputName = selectColumn.Alias
                        ?? ColumnNameResolver.GetRawName(selectColumn.Expression);

                    DataKind kind = ExpressionTypeResolver.ResolveType(
                        selectColumn.Expression, sourceSchema, session.FunctionRegistry) ?? DataKind.String;

                    outputColumns.Add(new ColumnInfo(outputName, kind, nullable: true));
                    if (selectColumn.Alias is not null)
                    {
                        aliasedPositions.Add(outputColumns.Count - 1);
                    }
                    break;
            }
        }

        string[] names = outputColumns.Select(column => column.Name).ToArray();
        ColumnNameResolver.DeduplicateNames(names, aliasedPositions);
        List<ColumnInfo> deduplicatedColumns = new(outputColumns.Count);
        for (int index = 0; index < outputColumns.Count; index++)
        {
            ColumnInfo original = outputColumns[index];
            deduplicatedColumns.Add(new ColumnInfo(names[index], original.Kind, original.Nullable));
        }

        return new Schema(deduplicatedColumns);
    }

    /// <summary>
    /// Returns a summary of all available dot-commands with usage and descriptions.
    /// </summary>
    private static CommandResult HandleHelp()
    {
        string helpText =
            """
            .help                          Show this help message
            .tables                        List all registered tables
            .schema <table>                Show column schema for a table
            .providers                     List registered format providers
            .functions                     List available scalar and table-valued functions
            .source <definition>           Add a data source (admin only)
            .explain <sql>                 Show the query execution plan
            .explain analyze <sql>         Run query and show plan with runtime metrics
            .sessions                      List all active sessions (admin only)
            .kill <session_id> [query_id]  Cancel query/queries on another session (admin only)
            .cancel [query_id]             Cancel a specific or all active queries on this session
            
            Any other input is executed as a SQL query.
            Source definition format: provider:name=path[;key=value;...]
            """;

        return CommandResult.Success(helpText);
    }

    private static CommandResult HandleListTables(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.Schema))
        {
            return CommandResult.Error("Permission denied: you are not authorized to inspect schemas.");
        }

        List<string> tables = session.Catalog.TableNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(DatumIngest.Parsing.Tokens.SqlIdentifier.QuoteIfNeeded)
            .ToList();
        return CommandResult.ListResult(tables);
    }

    private static async Task<CommandResult> HandleSchemaAsync(
        Session session, string tableName, CancellationToken cancellationToken)
    {
        if (!session.IsAuthorized(ServerOperation.Schema))
        {
            return CommandResult.Error("Permission denied: you are not authorized to inspect schemas.");
        }

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return CommandResult.Error("Usage: .schema <table_name>");
        }

        // Accept bracket/quote-delimited names so users can copy from .tables output.
        string resolvedName = DatumIngest.Parsing.Tokens.SqlIdentifier.Unquote(tableName);
        Schema schema = await session.Catalog.GetSchemaAsync(resolvedName, cancellationToken).ConfigureAwait(false);
        return CommandResult.SchemaResult(schema);
    }

    private static CommandResult HandleListProviders(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.Schema))
        {
            return CommandResult.Error("Permission denied: you are not authorized to inspect schemas.");
        }

        List<string> providers = session.Catalog.ProviderNames
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return CommandResult.ListResult(providers);
    }

    private static CommandResult HandleListFunctions(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.Schema))
        {
            return CommandResult.Error("Permission denied: you are not authorized to inspect schemas.");
        }

        List<string> functionNames = session.FunctionRegistry.ScalarFunctionNames
            .Concat(session.FunctionRegistry.TableValuedFunctionNames)
            .Concat(session.FunctionRegistry.AggregateFunctionNames)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<FunctionSignature> signatures = new(functionNames.Count);

        foreach (string name in functionNames)
        {
            FunctionSignature? documentation = FunctionDocumentation.TryGet(name);
            IScalarFunction? scalarFunction = session.FunctionRegistry.TryGetScalar(name);

            bool isAggregate = session.FunctionRegistry.TryGetAggregate(name) is not null;

            signatures.Add(new FunctionSignature
            {
                Name = documentation?.Name ?? name,
                Parameters = documentation?.Parameters ?? [],
                ReturnType = documentation?.ReturnType,
                Description = documentation?.Description,
                Category = documentation?.Category ?? FunctionCategory.Utility,
                IsTableValued = documentation?.IsTableValued ?? false,
                IsAggregate = isAggregate || (documentation?.IsAggregate ?? false),
                QueryUnitCost = scalarFunction?.QueryUnitCost ?? 0,
            });
        }

        return CommandResult.FunctionList(signatures);
    }

    /// <summary>
    /// Registers a data source, expands multi-table providers (e.g. root-object JSON
    /// files), and discovers sidecar manifests for the resulting tables.
    /// </summary>
    private static async Task<CommandResult> HandleAddSourceAsync(
        Session session, string definition, CancellationToken cancellationToken)
    {
        if (!session.IsAuthorized(ServerOperation.AddSource))
        {
            return CommandResult.Error("Permission denied: you are not authorized to add sources.");
        }

        if (string.IsNullOrWhiteSpace(definition))
        {
            return CommandResult.Error("Usage: .source <provider:name=path[;key=value;...]>");
        }

        TableDescriptor descriptor = ParseSourceDefinition(definition);
        await session.Catalog.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);

        // Auto-discover sidecar files (indexes, manifests, schemas) for the newly registered source.
        session.Catalog.DiscoverSidecars();

        // Report expansion when the original name was replaced.
        if (!session.Catalog.TryResolve(descriptor.Name, out _))
        {
            List<string> expandedNames = session.Catalog.TableNames
                .Where(name => name.StartsWith(descriptor.Name + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return CommandResult.Success(
                $"Source '{descriptor.Name}' expanded into {expandedNames.Count} table(s): " +
                $"{string.Join(", ", expandedNames)} ({descriptor.Provider}).");
        }

        return CommandResult.Success($"Source '{descriptor.Name}' registered ({descriptor.Provider}).");
    }

    private async Task<CommandResult> HandleExplainAsync(
        Session session, string sql, CancellationToken cancellationToken)
    {
        if (!session.IsAuthorized(ServerOperation.Explain))
        {
            return CommandResult.Error("Permission denied: you are not authorized to view execution plans.");
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return CommandResult.Error("Usage: .explain <sql_query>");
        }

        // Detect "analyze" prefix for EXPLAIN ANALYZE.
        bool analyze = sql.StartsWith("analyze ", StringComparison.OrdinalIgnoreCase);
        string actualSql = analyze ? sql["analyze ".Length..] : sql;

        QueryExpression query = SqlParser.Parse(actualSql);
        QueryPlanner planner = new(session.Catalog, session.FunctionRegistry);
        IQueryOperator plan = await planner.PlanAsync(query, cancellationToken).ConfigureAwait(false);

        ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);

        if (analyze)
        {
            InstrumentedOperator instrumentedRoot = InstrumentedOperator.InstrumentTree(plan);
            using LocalBufferPool localBufferPool = GlobalBufferPool.RentLocalBufferPool();
            ExecutionContext context = new(
                cancellationToken,
                session.FunctionRegistry,
                session.Catalog,
                localBufferPool,
                memoryBudgetBytes: session.Governor.MemoryBudgetBytes)
            {
                DegreeOfParallelism = Environment.ProcessorCount,
                ParallelismBudget = _parallelismBudget,
            };

            await foreach (Row _ in instrumentedRoot.ExecuteAsync(context).ConfigureAwait(false))
            {
                // Drain the stream to collect runtime metrics.
            }

            InstrumentedOperator.PopulateMetrics(explainPlan, instrumentedRoot);
        }

        return CommandResult.ExplainResult(explainPlan.Render(), explainPlan);
    }

    private CommandResult HandleListSessions(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.ListSessions))
        {
            return CommandResult.Error("Permission denied: you are not authorized to list sessions.");
        }

        IReadOnlyList<Session> sessions = _sessionManager.GetAllSessions();
        List<SessionInfo> infos = new();
        foreach (Session s in sessions)
        {
            infos.Add(new SessionInfo(
                s.SessionId,
                s.Role,
                s.DatasetId,
                s.CreatedAt,
                s.LastActivityAt,
                s.QueryHistory.Count,
                s.TotalQueryUnits));
        }

        return CommandResult.SessionList(infos);
    }

    private CommandResult HandleKillQuery(Session session, string argument)
    {
        if (!session.IsAuthorized(ServerOperation.KillQuery))
        {
            return CommandResult.Error("Permission denied: you are not authorized to kill queries.");
        }

        string[] parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0 || !Guid.TryParse(parts[0], out Guid targetId))
        {
            return CommandResult.Error("Usage: .kill <session_id> [query_id]");
        }

        Session? target = _sessionManager.GetSession(targetId);
        if (target is null)
        {
            return CommandResult.Error($"Session '{targetId}' not found.");
        }

        if (parts.Length > 1)
        {
            if (!Guid.TryParse(parts[1], out Guid queryId))
            {
                return CommandResult.Error("Usage: .kill <session_id> [query_id]");
            }

            if (!target.CancelQuery(queryId))
            {
                return CommandResult.Error($"Query '{queryId}' not found on session '{targetId}'.");
            }

            return CommandResult.Success($"Cancelled query '{queryId}' on session '{targetId}'.");
        }

        int count = target.CancelAllAndReset();
        return CommandResult.Success($"Cancelled {count} active query/queries on session '{targetId}'.");
    }

    private static CommandResult HandleCancelQuery(Session session, string argument)
    {
        if (!session.IsAuthorized(ServerOperation.CancelQuery))
        {
            return CommandResult.Error("Permission denied: you are not authorized to cancel queries.");
        }

        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (!Guid.TryParse(argument.Trim(), out Guid queryId))
            {
                return CommandResult.Error("Usage: .cancel [query_id]");
            }

            if (!session.CancelQuery(queryId))
            {
                return CommandResult.Error($"Query '{queryId}' not found.");
            }

            return CommandResult.Success($"Cancelled query '{queryId}'.");
        }

        int count = session.CancelAllAndReset();
        return CommandResult.Success($"Cancelled {count} active query/queries.");
    }

    /// <summary>
    /// Parses a source definition string into a table descriptor.
    /// Format: <c>provider:name=path[;key=value;...]</c> or <c>name=path</c>.
    /// </summary>
    internal static TableDescriptor ParseSourceDefinition(string source)
    {
        int colonIndex = source.IndexOf(':');
        int equalsIndex = source.IndexOf('=');

        if (equalsIndex < 0)
        {
            throw new ArgumentException(
                $"Invalid source format: '{source}'. Expected: provider:name=path or name=path");
        }

        bool hasExplicitProvider = colonIndex >= 0 && colonIndex < equalsIndex;

        string? provider;
        string remainder;

        if (hasExplicitProvider)
        {
            provider = source[..colonIndex];
            remainder = source[(colonIndex + 1)..];
        }
        else
        {
            provider = null;
            remainder = source;
        }

        int nameEqualsIndex = remainder.IndexOf('=');
        string rawName = remainder[..nameEqualsIndex];
        string name = FileFormatDetector.DeriveTableName(rawName);
        string pathAndOptions = remainder[(nameEqualsIndex + 1)..];

        Dictionary<string, string> options = new();
        string filePath;

        int semiIndex = pathAndOptions.IndexOf(';');
        if (semiIndex >= 0)
        {
            filePath = pathAndOptions[..semiIndex];
            string optionsPart = pathAndOptions[(semiIndex + 1)..];

            foreach (string pair in optionsPart.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int keyValueSplit = pair.IndexOf('=');
                if (keyValueSplit > 0)
                {
                    options[pair[..keyValueSplit]] = pair[(keyValueSplit + 1)..];
                }
            }
        }
        else
        {
            filePath = pathAndOptions;
        }

        // Detect compression from the file extension (e.g. .gz).
        CompressionKind compression = CompressionKind.None;
        string outerExtension = Path.GetExtension(filePath);
        if (outerExtension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            compression = CompressionKind.Gzip;
        }

        if (provider is null)
        {
            // Strip compression extension before detecting the inner format.
            string detectPath = compression != CompressionKind.None
                ? filePath[..^outerExtension.Length]
                : filePath;

            provider = FileFormatDetector.DetectProvider(detectPath)
                ?? throw new ArgumentException(
                    $"Cannot detect provider for '{filePath}'. " +
                    $"Supported formats: {FileFormatDetector.SupportedFormatList}. " +
                    "Use explicit format: provider:name=path");
        }

        return new TableDescriptor(provider, name, filePath, options, compression);
    }

    /// <summary>
    /// Extracts the leftmost <see cref="SelectStatement"/> from a query expression
    /// tree by walking the left branch of compound expressions. Used to derive the
    /// output schema (SQL set operations use the left branch's column names).
    /// </summary>
    private static SelectStatement ExtractLeftmostStatement(QueryExpression query)
    {
        QueryExpression current = query;
        while (current is CompoundQueryExpression compound)
        {
            current = compound.Left;
        }

        return ((SelectQueryExpression)current).Statement;
    }

    /// <summary>
    /// Wraps an <see cref="IAsyncEnumerable{Row}"/> so that the given
    /// <see cref="LocalBufferPool"/> is disposed after the stream finishes
    /// (or is abandoned). This ensures owned objects are returned even when
    /// the enumerable outlives the method that created the pool.
    /// </summary>
    private static async IAsyncEnumerable<Row> StreamWithDisposal(
        IAsyncEnumerable<Row> source, LocalBufferPool pool)
    {
        try
        {
            await foreach (Row row in source.ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            pool.Dispose();
        }
    }
}
