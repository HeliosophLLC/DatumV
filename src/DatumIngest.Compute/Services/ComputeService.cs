using DatumIngest.Compute.Grpc;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
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
        SessionRole role = ParseRole(request.Role);
        QueryGovernor governor = QueryGovernor.Merge(
            _serverDefaults,
            request.QueryTimeoutSeconds,
            request.MaxOutputRows,
            request.ThrottleDelayMs,
            request.MaxQueryUnits);

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
                    DatasetCatalogFactory.Create,
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
        };
    }

    /// <inheritdoc />
    public override Task<DestroySessionResponse> DestroySession(
        DestroySessionRequest request, ServerCallContext context)
    {
        Guid sessionId = ParseSessionId(request.SessionId);

        if (!_sessionManager.RemoveSession(sessionId))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session '{request.SessionId}' not found."));
        }

        return Task.FromResult(new DestroySessionResponse());
    }

    /// <inheritdoc />
    public override async Task Query(
        QueryRequest request,
        IServerStreamWriter<QueryRow> responseStream,
        ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        // Link the gRPC per-call token with the session token so that
        // KillQuery (which cancels the session token) stops the stream.
        using CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, session.CancellationToken);
        CancellationToken cancellationToken = linkedTokenSource.Token;

        // Apply query deadline if the session governor specifies one.
        QueryGovernor governor = session.Governor;
        if (governor.QueryTimeoutSeconds.HasValue)
        {
            linkedTokenSource.CancelAfter(TimeSpan.FromSeconds(governor.QueryTimeoutSeconds.Value));
        }

        // Create a per-query meter for QU cost tracking.
        QueryMeter meter = new(governor.MaxQueryUnits);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, request.Sql, cancellationToken, meter).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Query failed."));
        }

        if (result.Kind != CommandResultKind.StreamingRows || result.Rows is null || result.Schema is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Expected streaming rows result."));
        }

        bool schemaWritten = false;
        long rowCount = 0;
        long? maxRows = governor.MaxOutputRows;
        int? throttleMilliseconds = governor.ThrottleDelayMilliseconds;

        try
        {
            await foreach (Row row in result.Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rowCount++;

                if (maxRows.HasValue && rowCount > maxRows.Value)
                {
                    throw new RpcException(new Status(
                        StatusCode.ResourceExhausted,
                        $"Row budget exceeded (limit: {maxRows.Value})."));
                }

                if (meter.IsBudgetExceeded)
                {
                    throw new RpcException(new Status(
                        StatusCode.ResourceExhausted,
                        $"Query Unit budget exceeded (limit: {governor.MaxQueryUnits!.Value}, used: {meter.FunctionQueryUnits})."));
                }

                QueryRow queryRow = new();

                // Attach schema to the first row only.
                if (!schemaWritten)
                {
                    queryRow.Schema = ProtoConverter.ToProto(result.Schema);
                    schemaWritten = true;
                }

                for (int i = 0; i < row.FieldCount; i++)
                {
                    queryRow.Values.Add(ProtoConverter.ToProto(row[i]));
                }

                queryRow.QueryUnits = meter.FunctionQueryUnits;

                await responseStream.WriteAsync(queryRow, cancellationToken).ConfigureAwait(false);

                if (throttleMilliseconds.HasValue && rowCount % QueryGovernor.ThrottleBatchSize == 0)
                {
                    await Task.Delay(throttleMilliseconds.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            // The gRPC call was not cancelled by the client. If a deadline was
            // configured, report DeadlineExceeded; otherwise it was a KillQuery.
            if (governor.QueryTimeoutSeconds.HasValue)
            {
                throw new RpcException(new Status(
                    StatusCode.DeadlineExceeded,
                    $"Query exceeded the {governor.QueryTimeoutSeconds.Value}s deadline."));
            }

            throw new RpcException(new Status(StatusCode.Cancelled, "Query cancelled."));
        }
        finally
        {
            session.AddQueryUnits(meter.FunctionQueryUnits);
        }
    }

    /// <inheritdoc />
    public override async Task<SchemaResponse> GetSchema(
        GetSchemaRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CancellationToken cancellationToken = LinkedToken(context, session);

        CommandResult result = await _dispatcher.DispatchAsync(
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

    /// <inheritdoc />
    public override async Task<ListResponse> ListTables(
        ListTablesRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CommandResult result = await _dispatcher.DispatchAsync(
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

        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".explain {request.Sql}", LinkedToken(context, session)).ConfigureAwait(false);

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

        return new AddSourceResponse { Message = result.Message ?? "Source added." };
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

        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".kill {request.TargetSessionId}", LinkedToken(context, session)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Kill failed."));
        }

        return new KillQueryResponse { Message = result.Message ?? "Query cancelled." };
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
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// Creates a cancellation token that fires when either the gRPC call is cancelled
    /// or the session token is cancelled (via <see cref="Session.CancelAndReset"/>).
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
            "UInt8" => ParameterKindValue.ParameterKindUint8,
            "Scalar" => ParameterKindValue.ParameterKindScalar,
            "Vector" => ParameterKindValue.ParameterKindVector,
            "Matrix" => ParameterKindValue.ParameterKindMatrix,
            "Tensor" => ParameterKindValue.ParameterKindTensor,
            "UInt8Array" => ParameterKindValue.ParameterKindUint8Array,
            "Image" => ParameterKindValue.ParameterKindImage,
            "String" => ParameterKindValue.ParameterKindString,
            "Date" => ParameterKindValue.ParameterKindDate,
            "DateTime" => ParameterKindValue.ParameterKindDateTime,
            "JsonValue" => ParameterKindValue.ParameterKindJsonValue,
            _ => ParameterKindValue.ParameterKindAny,
        };
    }
}
