using DatumIngest.Compute.Grpc;
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

    /// <summary>
    /// Initializes the gRPC service with the shared session manager and command dispatcher.
    /// </summary>
    /// <param name="sessionManager">Session manager for session lifecycle.</param>
    /// <param name="dispatcher">Command dispatcher for query execution.</param>
    public ComputeService(SessionManager sessionManager, CommandDispatcher dispatcher)
    {
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public override async Task<CreateSessionResponse> CreateSession(
        CreateSessionRequest request, ServerCallContext context)
    {
        SessionRole role = ParseRole(request.Role);

        Session session;

        if (string.IsNullOrEmpty(request.DatasetId))
        {
            session = _sessionManager.CreateLocalSession(role, new DatumIngest.Catalog.TableCatalog());
        }
        else
        {
            try
            {
                session = await _sessionManager.CreateSessionAsync(
                    role,
                    request.DatasetId,
                    DatasetCatalogFactory.Create,
                    context.CancellationToken).ConfigureAwait(false);
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

        CommandResult result = await _dispatcher.DispatchAsync(
            session, request.Sql, context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Query failed."));
        }

        if (result.Kind != CommandResultKind.StreamingRows || result.Rows is null || result.Schema is null)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Expected streaming rows result."));
        }

        bool schemaWritten = false;

        await foreach (Row row in result.Rows.WithCancellation(context.CancellationToken).ConfigureAwait(false))
        {
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

            await responseStream.WriteAsync(queryRow, context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task<SchemaResponse> GetSchema(
        GetSchemaRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".schema {request.TableName}", context.CancellationToken).ConfigureAwait(false);

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
            session, ".tables", context.CancellationToken).ConfigureAwait(false);

        return ToListResponse(result);
    }

    /// <inheritdoc />
    public override async Task<ListResponse> ListProviders(
        ListProvidersRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CommandResult result = await _dispatcher.DispatchAsync(
            session, ".providers", context.CancellationToken).ConfigureAwait(false);

        return ToListResponse(result);
    }

    /// <inheritdoc />
    public override async Task<ListFunctionsResponse> ListFunctions(
        ListFunctionsRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);
        CommandResult result = await _dispatcher.DispatchAsync(
            session, ".functions", context.CancellationToken).ConfigureAwait(false);

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
            session, $".explain {request.Sql}", context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Explain failed."));
        }

        return new ExplainResponse { PlanText = result.Message ?? "" };
    }

    /// <inheritdoc />
    public override async Task<AddSourceResponse> AddSource(
        AddSourceRequest request, ServerCallContext context)
    {
        Session session = ResolveSession(request.SessionId);

        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".source {request.SourceDefinition}", context.CancellationToken).ConfigureAwait(false);

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
            session, ".sessions", context.CancellationToken).ConfigureAwait(false);

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
            session, $".kill {request.TargetSessionId}", context.CancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message ?? "Kill failed."));
        }

        return new KillQueryResponse { Message = result.Message ?? "Query cancelled." };
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
