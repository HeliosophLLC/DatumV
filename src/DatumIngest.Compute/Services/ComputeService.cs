using DatumIngest.Compute.Grpc;
using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Server;
using Grpc.Core;

namespace DatumIngest.Compute.Services;

/// <summary>
/// gRPC service implementation that delegates all operations to the
/// transport-agnostic <see cref="CommandDispatcher"/> engine.
/// </summary>
public sealed class ComputeService : DatumCompute.DatumComputeBase
{
    private readonly SessionManager _sessionManager;
    private readonly CommandDispatcher _dispatcher;
    private readonly QueryGovernor _serverDefaults;

    /// <summary>
    /// Initializes the gRPC service with the shared session manager, command dispatcher,
    /// and server-wide governance defaults.
    /// </summary>
    /// <param name="sessionManager">Session manager for session lifecycle.</param>
    /// <param name="dispatcher">Command dispatcher for query execution.</param>
    /// <param name="serverDefaults">Server-wide default resource governance limits.</param>
    public ComputeService(SessionManager sessionManager, CommandDispatcher dispatcher, QueryGovernor serverDefaults)
    {
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _serverDefaults = serverDefaults;
    }

    /// <inheritdoc />
    public override async Task<CreateSessionResponse> CreateSession(
        CreateSessionRequest request, ServerCallContext context)
    {
        // Reconnect path: broker passes the previous session ID on client reconnect.
        // If the session is still within its grace period, cancel the expiry and return it.
        if (!string.IsNullOrEmpty(request.ReconnectSessionId)
            && Guid.TryParse(request.ReconnectSessionId, out Guid reconnectId))
        {
            Session? existing = _sessionManager.GetSession(reconnectId);
            if (existing is not null)
            {
                _sessionManager.CancelSessionExpiry(reconnectId);
                return new CreateSessionResponse
                {
                    SessionId = existing.SessionId.ToString(),
                    Reconnected = true,
                };
            }
            // Grace period elapsed — session was already swept. Fall through to create a new one.
        }

        SessionRole role = ParseRole(request.Role);
        QueryGovernor governor = QueryGovernor.Merge(
            _serverDefaults,
            request.QueryTimeoutSeconds,
            request.MaxOutputRows,
            request.ThrottleDelayMs,
            request.MaxQueryUnits,
            request.MemoryBudgetBytes,
            request.MaxConcurrentQueries);

        Session session;

        if (string.IsNullOrEmpty(request.DatasetId))
        {
            session = _sessionManager.CreateLocalSession(role, new DatumIngest.Catalog.TableCatalog(), governor);
        }
        else
        {
            try
            {
                session = await _sessionManager.CreateSessionAsync(
                    role,
                    request.DatasetId,
                    DatasetCatalogFactory.CreateAsync,
                    context.CancellationToken,
                    governor).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "No IDatasetStore is configured. Register an IDatasetStore before calling AddDatumCompute to use dataset-backed sessions."));
            }
        }

        return new CreateSessionResponse
        {
            SessionId = session.SessionId.ToString(),
            Reconnected = false,
        };
    }

    /// <summary>
    /// Grace period for deferred session destruction. Sessions remain accessible
    /// to reconnecting clients until this window elapses after <see cref="DestroySession"/>.
    /// </summary>
    private static readonly TimeSpan DefaultExpiryGrace = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override Task<DestroySessionResponse> DestroySession(
        DestroySessionRequest request, ServerCallContext context)
    {
        Guid sessionId = ParseSessionId(request.SessionId);

        try
        {
            _sessionManager.BeginSessionExpiry(sessionId, DefaultExpiryGrace);
        }
        catch (InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session '{request.SessionId}' not found."));
        }

        return Task.FromResult(new DestroySessionResponse());
    }

    /// <inheritdoc />
    public override Task<CreateQueryContextResponse> CreateQueryContext(
        CreateQueryContextRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext queryContext = session.CreateQueryContext(request.Label);

        return Task.FromResult(new CreateQueryContextResponse
        {
            ContextId = queryContext.ContextId.ToString(),
        });
    }

    /// <inheritdoc />
    public override Task<DestroyQueryContextResponse> DestroyQueryContext(
        DestroyQueryContextRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        Guid contextId = ParseContextId(request.ContextId);

        if (!session.DestroyQueryContext(contextId))
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Query context '{request.ContextId}' not found on session '{request.SessionId}'."));
        }

        return Task.FromResult(new DestroyQueryContextResponse());
    }

    /// <inheritdoc />
    public override Task<ListQueryContextsResponse> ListQueryContexts(
        ListQueryContextsRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        ListQueryContextsResponse response = new();

        foreach (QueryContext queryContext in session.GetQueryContexts())
        {
            response.Contexts.Add(new QueryContextInfoMessage
            {
                ContextId = queryContext.ContextId.ToString(),
                Label = queryContext.Label,
                CreatedAt = queryContext.CreatedAt.ToString("O"),
                TempTableCount = queryContext.TempTableCount,
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public override async Task Query(
        QueryRequest request,
        IServerStreamWriter<QueryResult> responseStream,
        ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext queryContext = ResolveQueryContext(session, request.ContextId);

        // Register a per-query cancellation scope so concurrent queries
        // can be cancelled independently.
        ActiveQuery activeQuery;
        try
        {
            activeQuery = session.RegisterQuery(request.Sql, queryContext.ContextId);
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, exception.Message));
        }

        string queryIdString = activeQuery.QueryId.ToString();

        try
        {
            ReferenceStore.BeginQueryScope();

            // Link the gRPC per-call token, the session-level token, and
            // the per-query token so that any of the three can stop the stream.
            using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken, session.CancellationToken, activeQuery.CancellationToken);
            CancellationToken cancellationToken = linkedTokenSource.Token;

            // Apply query deadline if the session governor specifies one.
            QueryGovernor governor = session.Governor;
            if (governor.QueryTimeoutSeconds.HasValue)
            {
                linkedTokenSource.CancelAfter(TimeSpan.FromSeconds(governor.QueryTimeoutSeconds.Value));
            }

            // Single meter and memory budget across the entire batch.
            QueryMeter meter = new(governor.MaxQueryUnits);

            // Convert gRPC parameter bindings to domain DataValue dictionary.
            Dictionary<string, DataValue>? parameters = null;
            if (request.Parameters.Count > 0)
            {
                parameters = new Dictionary<string, DataValue>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, DataValueMessage> entry in request.Parameters)
                {
                    parameters[entry.Key] = ProtoConverter.FromProto(entry.Value);
                }
            }

            // Parse the SQL as a batch (supports single and multi-statement).
            // Any tokenizer or parser exception is a user-provided syntax error and
            // must be surfaced as InvalidArgument, not escape as StatusCode.Unknown.
            IReadOnlyList<Statement> statements;
            try
            {
                statements = SqlParser.ParseBatch(request.Sql);
            }
            catch (Exception parseException) when (parseException is not OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Syntax error: {parseException.Message}"));
            }

            session.RecordQuery(request.Sql);

            long totalRowCount = 0;
            long? maxRows = request.MaxRows > 0 ? request.MaxRows
                : request.MaxRows < 0 ? null
                : governor.MaxOutputRows;
            int? throttleMilliseconds = governor.ThrottleDelayMilliseconds;

            // Capture a single batch clock so all statements in this batch share the same
            // CURRENT_TIMESTAMP / now() value, matching PostgreSQL's transaction-stable semantics.
            DateTimeOffset batchClock = DateTimeOffset.UtcNow;

            try
            {
                for (int statementIndex = 0; statementIndex < statements.Count; statementIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ExecutionTracer.Write($"GRPC dispatching statement #{statementIndex}  sql={request.Sql[..Math.Min(request.Sql.Length, 120)]}");
                    CommandResult result = await _dispatcher.DispatchStatementAsync(
                        session, queryContext, statements[statementIndex], cancellationToken, meter, parameters, batchClock).ConfigureAwait(false);
                    ExecutionTracer.Write($"GRPC dispatch returned  kind={result.Kind}  success={result.IsSuccess}");

                    if (!result.IsSuccess)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Statement failed."));
                    }

                    if (result.Kind == CommandResultKind.AffectedRows)
                    {
                        // DDL/DML — emit a single effect message.
                        QueryResult effectMessage = new()
                        {
                            StatementIndex = statementIndex,
                            QueryUnits = meter.QueryUnits,
                            QueryId = queryIdString,
                            Effect = new StatementEffect
                            {
                                AffectedRows = result.AffectedRowCount ?? 0,
                                Message = result.Message ?? "",
                            },
                        };
                        await responseStream.WriteAsync(effectMessage, cancellationToken).ConfigureAwait(false);
                    }
                    else if (result.Kind == CommandResultKind.StreamingRows && result.Rows is not null && result.Schema is not null)
                    {
                        // Query — stream rows with schema on the first row.
                        bool schemaWritten = false;
                        ExecutionTracer.Write("GRPC entering row streaming loop");

                        await foreach (RowBatch batch in result.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                        {
                            for (int rowIndex = 0; rowIndex < batch.Count; rowIndex++)
                            {
                                Row row = batch[rowIndex];
                                totalRowCount++;

                                if (maxRows.HasValue && totalRowCount > maxRows.Value)
                                {
                                    throw new RpcException(new Status(
                                        StatusCode.ResourceExhausted,
                                        $"Row budget exceeded (limit: {maxRows.Value})."));
                                }

                                if (meter.IsBudgetExceeded)
                                {
                                    throw new RpcException(new Status(
                                        StatusCode.ResourceExhausted,
                                        $"Query Unit budget exceeded (limit: {governor.MaxQueryUnits!.Value}, used: {meter.QueryUnits})."));
                                }

                                QueryResultRow queryRow = new();

                                if (!schemaWritten)
                                {
                                    queryRow.Schema = ProtoConverter.ToProto(result.Schema);
                                    schemaWritten = true;
                                }

                                for (int columnIndex = 0; columnIndex < row.FieldCount; columnIndex++)
                                {
                                    queryRow.Values.Add(ProtoConverter.ToProto(row[columnIndex]));
                                }

                                QueryResult rowMessage = new()
                                {
                                    StatementIndex = statementIndex,
                                    QueryUnits = meter.QueryUnits,
                                    QueryId = queryIdString,
                                    Row = queryRow,
                                };

                                await responseStream.WriteAsync(rowMessage, cancellationToken).ConfigureAwait(false);

                                if (throttleMilliseconds.HasValue && totalRowCount % QueryGovernor.ThrottleBatchSize == 0)
                                {
                                    await Task.Delay(throttleMilliseconds.Value, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            batch.Return();
                        }

                        // Emit assertion diagnostics if any WARN or SKIP conditions fired during execution.
                        AssertionDiagnostics? assertionDiagnostics = result.AssertionDiagnostics;
                        if (assertionDiagnostics is not null
                            && (assertionDiagnostics.WarnedRowCount > 0 || assertionDiagnostics.SkippedRowCount > 0))
                        {
                            AssertionDiagnosticsMessage diagnosticsMessage = new()
                            {
                                WarnedRowCount = assertionDiagnostics.WarnedRowCount,
                                SkippedRowCount = assertionDiagnostics.SkippedRowCount,
                            };
                            diagnosticsMessage.SampleMessages.AddRange(assertionDiagnostics.SampleMessages);

                            QueryResult diagnosticsResult = new()
                            {
                                StatementIndex = statementIndex,
                                QueryUnits = meter.QueryUnits,
                                QueryId = queryIdString,
                                Diagnostics = diagnosticsMessage,
                            };
                            await responseStream.WriteAsync(diagnosticsResult, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                // The gRPC call was not cancelled by the client. If a deadline was
                // configured, report DeadlineExceeded; otherwise it was a cancel/kill.
                if (governor.QueryTimeoutSeconds.HasValue)
                {
                    throw new RpcException(new Status(
                        StatusCode.DeadlineExceeded,
                        $"Query exceeded the {governor.QueryTimeoutSeconds.Value}s deadline."));
                }

                throw new RpcException(new Status(StatusCode.Cancelled, "Query cancelled."));
            }
            catch (OperationCanceledException)
            {
                // The gRPC call itself was cancelled (client disconnect).
                throw new RpcException(new Status(StatusCode.Cancelled, "Query cancelled by client."));
            }
            catch (QueryBudgetExceededException budgetException)
            {
                throw new RpcException(new Status(
                    StatusCode.ResourceExhausted,
                    budgetException.Message));
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    $"Query failed at row {totalRowCount}: {exception.GetType().Name}: {exception.Message}"));
            }
            finally
            {
                session.AddQueryUnits(meter.QueryUnits);
            }
        }
        finally
        {
            ReferenceStore.EndQueryScope();
            session.UnregisterQuery(activeQuery.QueryId);
        }
    }

    /// <inheritdoc />
    public override async Task<SchemaResponse> GetSchema(
        GetSchemaRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext? queryContext = TryResolveQueryContext(session, request.ContextId);
        CancellationToken cancellationToken = LinkedToken(context, session);

        ReferenceStore.BeginQueryScope();
        try
        {
            CommandResult result = queryContext is not null
                ? await _dispatcher.DispatchAsync(
                    session, queryContext, $".schema {request.TableName}", cancellationToken).ConfigureAwait(false)
                : await _dispatcher.DispatchAsync(
                    session, $".schema {request.TableName}", cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Schema lookup failed."));
            }

            if (result.Schema is null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Schema not found for '{request.TableName}'."));
            }

            SchemaResponse response = new();
            foreach (ColumnInfo column in result.Schema.Columns)
            {
                response.Columns.Add(ProtoConverter.ToProto(column));
            }

            return response;
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    /// <inheritdoc />
    public override async Task<ListResponse> ListTables(
        ListTablesRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext? queryContext = TryResolveQueryContext(session, request.ContextId);

        CommandResult result = queryContext is not null
            ? await _dispatcher.DispatchAsync(
                session, queryContext, ".tables", LinkedToken(context, session)).ConfigureAwait(false)
            : await _dispatcher.DispatchAsync(
                session, ".tables", LinkedToken(context, session)).ConfigureAwait(false);

        return ToListResponse(result);
    }

    /// <inheritdoc />
    public override async Task<ListResponse> ListProviders(
        ListProvidersRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CommandResult result = await _dispatcher.DispatchAsync(
            session, ".providers", LinkedToken(context, session)).ConfigureAwait(false);

        return ToListResponse(result);
    }

    /// <inheritdoc />
    public override async Task<ListFunctionsResponse> ListFunctions(
        ListFunctionsRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CommandResult result = await _dispatcher.DispatchAsync(
            session, ".functions", LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "List functions failed."));
        }

        ListFunctionsResponse response = new();

        if (result.Functions is not null)
        {
            foreach (FunctionSignature function in result.Functions)
            {
                FunctionInfoMessage info = new()
                {
                    Name = function.Name,
                    ReturnType = function.ReturnType ?? "",
                    IsTableValued = function.IsTableValued,
                    QueryUnitCost = function.QueryUnitCost,
                    Category = ToFunctionCategory(function.Category),
                };

                foreach (ParameterSignature parameter in function.Parameters)
                {
                    info.Parameters.Add(new ParameterInfoMessage
                    {
                        Name = parameter.Name,
                        Kind = ToParameterKind(parameter.Kind),
                        Required = !parameter.IsOptional,
                    });
                }

                response.Functions.Add(info);
            }
        }

        return response;
    }

    /// <inheritdoc />
    public override async Task<ExplainResponse> Explain(
        ExplainRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext? queryContext = TryResolveQueryContext(session, request.ContextId);

        string explainSql = request.Analyze ? $"analyze {request.Sql}" : request.Sql;

        ReferenceStore.BeginQueryScope();
        try
        {
            CommandResult result = queryContext is not null
                ? await _dispatcher.DispatchAsync(
                    session, queryContext, $".explain {explainSql}", LinkedToken(context, session)).ConfigureAwait(false)
                : await _dispatcher.DispatchAsync(
                    session, $".explain {explainSql}", LinkedToken(context, session)).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Explain failed."));
            }

            ExplainResponse response = new() { PlanText = result.Message ?? "" };

            if (result.ExplainPlan is not null)
            {
                response.Root = ToProto(result.ExplainPlan);
            }

            return response;
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    /// <summary>
    /// Recursively maps an <see cref="ExplainPlanNode"/> tree to its protobuf representation.
    /// </summary>
    private static ExplainPlanNodeMessage ToProto(ExplainPlanNode node)
    {
        ExplainPlanNodeMessage message = new()
        {
            OperatorName = node.OperatorName,
            Details = node.Details,
            ChildLabel = node.ChildLabel ?? "",
        };

        message.Warnings.AddRange(node.Warnings);
        message.Annotations.AddRange(node.Annotations);

        if (node.EstimatedRows.HasValue)
        {
            message.EstimatedRows = node.EstimatedRows.Value;
            message.HasEstimatedRows = true;
        }

        if (node.AccessStrategyMethod.HasValue)
        {
            message.AccessMethod = node.AccessStrategyMethod.Value switch
            {
                AccessMethod.TableScan => ExplainAccessMethod.TableScan,
                AccessMethod.IndexScan => ExplainAccessMethod.IndexScan,
                _ => ExplainAccessMethod.Unspecified,
            };
        }

        if (node.Properties is not null)
        {
            foreach (KeyValuePair<string, string> entry in node.Properties)
            {
                message.Properties[entry.Key] = entry.Value;
            }
        }

        foreach (ExplainPlanNode child in node.Children)
        {
            message.Children.Add(ToProto(child));
        }

        if (node.RowsProduced.HasValue || node.SelfTime.HasValue)
        {
            ExplainRuntimeMetrics runtime = new()
            {
                RowsProduced = node.RowsProduced ?? 0,
                RowsConsumed = node.RowsConsumed ?? 0,
                SelfTimeUs = node.SelfTime.HasValue ? (long)node.SelfTime.Value.TotalMicroseconds : 0,
                TotalTimeUs = node.TotalTime.HasValue ? (long)node.TotalTime.Value.TotalMicroseconds : 0,
            };
            runtime.RuntimeAnnotations.AddRange(node.RuntimeAnnotations);
            message.Runtime = runtime;
        }

        return message;
    }

    /// <inheritdoc />
    public override async Task<AddSourceResponse> AddSource(
        AddSourceRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".source {request.SourceDefinition}", LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Add source failed."));
        }

        return new AddSourceResponse
        {
            Message = result.Message ?? "Source added.",
        };
    }

    /// <inheritdoc />
    public override async Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, ".sessions", LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, result.Message ?? "Permission denied."));
        }

        ListSessionsResponse response = new();

        if (result.Sessions is not null)
        {
            foreach (SessionInfo info in result.Sessions)
            {
                response.Sessions.Add(new SessionInfoMessage
                {
                    SessionId = info.SessionId.ToString(),
                    Role = info.Role.ToString(),
                    DatasetId = info.DatasetId ?? "",
                    CreatedAt = info.CreatedAt.ToString("O"),
                    LastActivityAt = info.LastActivityAt.ToString("O"),
                    QueryCount = info.QueryCount,
                    TotalQueryUnits = info.TotalQueryUnits,
                });
            }
        }

        return response;
    }

    /// <inheritdoc />
    public override async Task<KillQueryResponse> KillQuery(
        KillQueryRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        string command = string.IsNullOrEmpty(request.QueryId)
            ? $".kill {request.TargetSessionId}"
            : $".kill {request.TargetSessionId} {request.QueryId}";

        CommandResult result = await _dispatcher.DispatchAsync(
            session, command, LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Kill failed."));
        }

        return new KillQueryResponse { Message = result.Message ?? "Query cancelled." };
    }

    /// <inheritdoc />
    public override async Task<CancelQueryResponse> CancelQuery(
        CancelQueryRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        QueryContext queryContext = ResolveQueryContext(session, request.ContextId);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, queryContext, ".cancel", LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Cancel failed."));
        }

        return new CancelQueryResponse { Message = result.Message ?? "Active query cancelled." };
    }

    /// <inheritdoc />
    public override Task<GetUsageResponse> GetUsage(
        GetUsageRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        GetUsageResponse response = new()
        {
            SessionId = session.SessionId.ToString(),
            TotalQueryUnits = session.TotalQueryUnits,
            QueryCount = session.QueryHistory.Count,
            ActiveQueryCount = session.GetActiveQueries().Count,
        };

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public override Task<ListActiveQueriesResponse> ListActiveQueries(
        ListActiveQueriesRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        ListActiveQueriesResponse response = new();

        foreach (ActiveQuery activeQuery in session.GetActiveQueries())
        {
            response.Queries.Add(new ActiveQueryMessage
            {
                QueryId = activeQuery.QueryId.ToString(),
                Sql = activeQuery.Sql,
                StartedAt = activeQuery.StartedAt.ToString("O"),
                ContextId = activeQuery.ContextId.ToString(),
            });
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public override Task<GetStatsResponse> GetStats(
        GetStatsRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        Dictionary<string, QueryResultsManifest> tables = new();

        foreach (string tableName in session.Catalog.TableNames)
        {
            if (session.Catalog.TryGetManifest(tableName, out QueryResultsManifest? manifest)
                && manifest is not null)
            {
                tables[tableName] = manifest;
            }
        }

        string manifestJson = tables.Count > 0
            ? ManifestSerializer.Serialize(new SourceManifest { Tables = tables })
            : "";

        return Task.FromResult(new GetStatsResponse { ManifestJson = manifestJson });
    }

    /// <summary>
    /// Creates a cancellation token that fires when either the gRPC call is cancelled
    /// or the session token is cancelled (via <see cref="Session.CancelAllAndReset"/>).
    /// </summary>
    /// <remarks>
    /// For the streaming <see cref="Query"/> method the linked source is managed with
    /// <c>using</c> so it is disposed deterministically. For unary RPCs the short-lived
    /// linked source is collected with the request scope.
    /// </remarks>
    private static CancellationToken LinkedToken(ServerCallContext context, Session session)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, session.CancellationToken).Token;
    }

    /// <summary>
    /// Resolves a session by its string identifier, throwing gRPC <see cref="StatusCode.NotFound"/>
    /// if not found.
    /// </summary>
    private Session ResolveSession(string sessionIdText)
    {
        if (!Guid.TryParse(sessionIdText, out Guid sessionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid session ID format."));
        }

        Session? session = _sessionManager.GetSession(sessionId);
        if (session is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session '{sessionIdText}' not found."));
        }

        return session;
    }

    /// <summary>
    /// Parses a role string into a <see cref="SessionRole"/>, defaulting to <see cref="SessionRole.User"/>.
    /// </summary>
    private static SessionRole ParseRole(string role)
    {
        return role.Equals("admin", StringComparison.OrdinalIgnoreCase)
            ? SessionRole.Admin
            : SessionRole.User;
    }

    /// <summary>
    /// Parses a session ID string into a <see cref="Guid"/>.
    /// </summary>
    private static Guid ParseSessionId(string sessionIdText)
    {
        if (!Guid.TryParse(sessionIdText, out Guid sessionId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid session ID format."));
        }

        return sessionId;
    }

    /// <summary>
    /// Parses a context ID string into a <see cref="Guid"/>.
    /// </summary>
    private static Guid ParseContextId(string contextIdText)
    {
        if (!Guid.TryParse(contextIdText, out Guid contextId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid context ID format."));
        }

        return contextId;
    }

    /// <summary>
    /// Resolves a <see cref="QueryContext"/> by its string identifier within the given session.
    /// </summary>
    private static QueryContext ResolveQueryContext(Session session, string contextIdText)
    {
        Guid contextId = ParseContextId(contextIdText);
        QueryContext? queryContext = session.GetQueryContext(contextId);

        if (queryContext is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Query context '{contextIdText}' not found in session '{session.SessionId}'."));
        }

        return queryContext;
    }

    /// <summary>
    /// Optionally resolves a <see cref="QueryContext"/> when the context ID is provided.
    /// Returns <see langword="null"/> when <paramref name="contextIdText"/> is empty,
    /// allowing catalog RPCs to fall back to the session-level catalog.
    /// </summary>
    private static QueryContext? TryResolveQueryContext(Session session, string contextIdText)
    {
        if (string.IsNullOrEmpty(contextIdText))
        {
            return null;
        }

        return ResolveQueryContext(session, contextIdText);
    }

    /// <summary>
    /// Converts a <see cref="CommandResult"/> with items into a <see cref="ListResponse"/>.
    /// </summary>
    private static ListResponse ToListResponse(CommandResult result)
    {
        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "List operation failed."));
        }

        ListResponse response = new();
        if (result.Items is not null)
        {
            response.Items.AddRange(result.Items);
        }

        return response;
    }

    /// <summary>
    /// Maps a <see cref="ParameterSignature.Kind"/> string to the
    /// corresponding <see cref="ParameterKindValue"/> enum value.
    /// </summary>
    private static ParameterKindValue ToParameterKind(string kind)
    {
        return kind switch
        {
            "Unknown" => ParameterKindValue.ParameterKindUnknown,
            "Type" => ParameterKindValue.ParameterKindType,
            "Boolean" => ParameterKindValue.ParameterKindBoolean,
            "UInt8" => ParameterKindValue.ParameterKindUint8,
            "UInt16" => ParameterKindValue.ParameterKindUint16,
            "UInt32" => ParameterKindValue.ParameterKindUint32,
            "UInt64" => ParameterKindValue.ParameterKindUint64,
            "Int8" => ParameterKindValue.ParameterKindInt8,
            "Int16" => ParameterKindValue.ParameterKindInt16,
            "Int32" => ParameterKindValue.ParameterKindInt32,
            "Int64" => ParameterKindValue.ParameterKindInt64,
            "Float32" => ParameterKindValue.ParameterKindFloat32,
            "Float64" => ParameterKindValue.ParameterKindFloat64,
            "Date" => ParameterKindValue.ParameterKindDate,
            "Time" => ParameterKindValue.ParameterKindTime,
            "DateTime" => ParameterKindValue.ParameterKindDateTime,
            "Duration" => ParameterKindValue.ParameterKindDuration,
            "String" => ParameterKindValue.ParameterKindString,
            "JsonValue" => ParameterKindValue.ParameterKindJsonValue,
            "Uuid" => ParameterKindValue.ParameterKindUuid,
            "UInt8Array" => ParameterKindValue.ParameterKindUint8Array,
            "Image" => ParameterKindValue.ParameterKindImage,
            "Vector" => ParameterKindValue.ParameterKindVector,
            "Matrix" => ParameterKindValue.ParameterKindMatrix,
            "Tensor" => ParameterKindValue.ParameterKindTensor,
            "Array" => ParameterKindValue.ParameterKindArray,
            "Struct" => ParameterKindValue.ParameterKindStruct,
            _ => ParameterKindValue.ParameterKindAny,
        };
    }

    private static FunctionCategoryValue ToFunctionCategory(FunctionCategory category)
    {
        return category switch
        {
            FunctionCategory.String => FunctionCategoryValue.FunctionCategoryString,
            FunctionCategory.Temporal => FunctionCategoryValue.FunctionCategoryTemporal,
            FunctionCategory.Numeric => FunctionCategoryValue.FunctionCategoryNumeric,
            FunctionCategory.Activation => FunctionCategoryValue.FunctionCategoryActivation,
            FunctionCategory.Vector => FunctionCategoryValue.FunctionCategoryVector,
            FunctionCategory.Image => FunctionCategoryValue.FunctionCategoryImage,
            FunctionCategory.Encoding => FunctionCategoryValue.FunctionCategoryEncoding,
            FunctionCategory.Json => FunctionCategoryValue.FunctionCategoryJson,
            FunctionCategory.Conversion => FunctionCategoryValue.FunctionCategoryConversion,
            FunctionCategory.Utility => FunctionCategoryValue.FunctionCategoryUtility,
            FunctionCategory.Table => FunctionCategoryValue.FunctionCategoryTable,
            FunctionCategory.Aggregate => FunctionCategoryValue.FunctionCategoryAggregate,
            _ => FunctionCategoryValue.FunctionCategoryUtility,
        };
    }
}
