using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
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

    /// <summary>
    /// Initializes a new command dispatcher.
    /// </summary>
    /// <param name="sessionManager">Session manager for session-level operations (list, kill).</param>
    public CommandDispatcher(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
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
            ".cancel" => HandleCancelQuery(session),
            ".join-suggestions" => HandleJoinSuggestions(session),
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

        SelectStatement statement = SqlParser.Parse(input);

        // Bind named parameters ($name) to concrete literal values before planning.
        if (parameters is not null && parameters.Count > 0)
        {
            statement = ParameterBinder.Bind(statement, parameters);
        }

        QueryPlanner planner = new(session.Catalog, session.FunctionRegistry);
        IQueryOperator plan = await planner.PlanAsync(statement, cancellationToken).ConfigureAwait(false);

        // Resolve schema from the first row's structure. We create the context
        // and wrap execution so the schema can be captured.
        ExecutionContext context = new(cancellationToken, session.FunctionRegistry, session.Catalog, queryMeter,
            memoryBudgetBytes: session.Governor.MemoryBudgetBytes);
        IAsyncEnumerable<Row> rows = plan.ExecuteAsync(context);

        // We need the schema before streaming. Peek at the plan to get column metadata.
        Schema schema = await ResolveQuerySchemaAsync(session, statement, cancellationToken).ConfigureAwait(false);

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
            .sessions                      List all active sessions (admin only)
            .kill <session_id>             Cancel a query on another session (admin only)
            .cancel                        Cancel the active query on this session
            .join-suggestions              Show cross-manifest join suggestions
            
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
                .Where(name => name.StartsWith(descriptor.Name + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return CommandResult.Success(
                $"Source '{descriptor.Name}' expanded into {expandedNames.Count} table(s): " +
                $"{string.Join(", ", expandedNames)} ({descriptor.Provider}).");
        }

        return CommandResult.Success($"Source '{descriptor.Name}' registered ({descriptor.Provider}).");
    }

    private static async Task<CommandResult> HandleExplainAsync(
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

        SelectStatement statement = SqlParser.Parse(sql);
        QueryPlanner planner = new(session.Catalog, session.FunctionRegistry);
        IQueryOperator plan = await planner.PlanAsync(statement, cancellationToken).ConfigureAwait(false);

        ExplainPlanNode explainPlan = QueryExplainer.Explain(plan);
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

    private CommandResult HandleKillQuery(Session session, string sessionIdText)
    {
        if (!session.IsAuthorized(ServerOperation.KillQuery))
        {
            return CommandResult.Error("Permission denied: you are not authorized to kill queries.");
        }

        if (!Guid.TryParse(sessionIdText.Trim(), out Guid targetId))
        {
            return CommandResult.Error("Usage: .kill <session_id>");
        }

        Session? target = _sessionManager.GetSession(targetId);
        if (target is null)
        {
            return CommandResult.Error($"Session '{targetId}' not found.");
        }

        target.CancelAndReset();
        return CommandResult.Success($"Cancelled active query on session '{targetId}'.");
    }

    private static CommandResult HandleCancelQuery(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.CancelQuery))
        {
            return CommandResult.Error("Permission denied: you are not authorized to cancel queries.");
        }

        session.CancelAndReset();
        return CommandResult.Success("Active query cancelled.");
    }

    /// <summary>
    /// Handles the <c>.join-suggestions</c> meta-command by computing (or returning cached)
    /// cross-manifest join analysis and formatting the results as a human-readable summary.
    /// </summary>
    private static CommandResult HandleJoinSuggestions(Session session)
    {
        if (!session.IsAuthorized(ServerOperation.Schema))
        {
            return CommandResult.Error("Permission denied: you are not authorized to inspect schemas.");
        }

        if (!session.Catalog.HasJoinSuggestions)
        {
            return CommandResult.Error("At least two tables with manifests are required for join suggestions.");
        }

        CrossManifestResult? result = session.Catalog.GetOrComputeCrossManifest(forceCompute: true);

        if (result is null)
        {
            return CommandResult.Error("Could not compute cross-manifest analysis.");
        }

        System.Text.StringBuilder output = new();
        output.AppendLine($"Cross-manifest join analysis across {result.Tables.Count} tables: {string.Join(", ", result.Tables)}");
        output.AppendLine();

        if (result.Candidates.Count == 0)
        {
            output.AppendLine("No join candidates discovered.");
            return CommandResult.Success(output.ToString());
        }

        output.AppendLine($"  {result.Candidates.Count} join candidate(s):");

        foreach (JoinCandidate candidate in result.Candidates)
        {
            string leftColumns = string.Join(", ", candidate.LeftColumns);
            string rightColumns = string.Join(", ", candidate.RightColumns);
            output.AppendLine($"    {candidate.LeftTable}.({leftColumns}) = {candidate.RightTable}.({rightColumns})  [{candidate.EstimatedJoinType}, confidence={candidate.Confidence:F2}]");

            if (candidate.QualityWarnings is { Count: > 0 })
            {
                output.AppendLine($"      Warnings: {string.Join("; ", candidate.QualityWarnings)}");
            }
        }

        if (result.TransitiveChains is { Count: > 0 })
        {
            output.AppendLine();
            output.AppendLine($"  {result.TransitiveChains.Count} transitive chain(s):");

            foreach (JoinChain chain in result.TransitiveChains)
            {
                output.AppendLine($"    {string.Join(" → ", chain.Tables)}  [min confidence={chain.MinConfidence:F2}]");
            }
        }

        if (result.Insights is { Count: > 0 })
        {
            output.AppendLine();
            output.AppendLine($"  {result.Insights.Count} insight(s):");

            foreach (Manifest.Insights.DatasetInsight insight in result.Insights)
            {
                output.AppendLine($"    [{insight.Severity}] {insight.Observation}");
            }
        }

        if (result.RecommendedQuery is not null)
        {
            output.AppendLine();
            output.AppendLine("  Suggested JOIN query:");
            output.AppendLine(result.RecommendedQuery);
        }

        return CommandResult.Success(output.ToString());
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
        string name = remainder[..nameEqualsIndex];
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

        provider ??= DetectProviderFromPath(filePath);

        return new TableDescriptor(provider, name, filePath, options);
    }

    private static string DetectProviderFromPath(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".csv" => "csv",
            ".json" => "json",
            ".jsonl" => "jsonl",
            ".parquet" => "parquet",
            ".hdf5" or ".h5" => "hdf5",
            ".zip" => "zip",
            _ => throw new ArgumentException(
                $"Cannot detect provider for '{filePath}'. Use explicit format: provider:name=path"),
        };
    }
}
