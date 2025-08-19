using DatumIngest.Compute.Grpc;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace DatumIngest.Compute.Client;

/// <summary>
/// Convenience wrapper that creates a <see cref="GrpcChannel"/> and
/// configures the generated <see cref="DatumCompute.DatumComputeClient"/>
/// with optional API key authentication. Disposes the underlying channel
/// when the connection is disposed.
/// </summary>
/// <remarks>
/// For advanced scenarios (custom channel options, dependency injection),
/// use the generated <see cref="DatumCompute.DatumComputeClient"/> directly
/// with your own <see cref="GrpcChannel"/>.
/// </remarks>
public sealed class DatumComputeConnection : IDisposable
{
    private readonly GrpcChannel _channel;

    /// <summary>
    /// Gets the generated gRPC client for calling the DatumCompute service.
    /// </summary>
    public DatumCompute.DatumComputeClient Client { get; }

    /// <summary>
    /// Creates a connection to the DatumCompute gRPC service.
    /// </summary>
    /// <param name="address">
    /// The gRPC server address (e.g. <c>"https://localhost:5001"</c>).
    /// </param>
    /// <param name="apiKey">
    /// Optional API key. When provided, added as an <c>x-api-key</c> metadata
    /// header on every call.
    /// </param>
    public DatumComputeConnection(string address, string? apiKey = null)
    {
        _channel = GrpcChannel.ForAddress(address);

        CallInvoker invoker = _channel.CreateCallInvoker();

        if (!string.IsNullOrEmpty(apiKey))
        {
            invoker = invoker.Intercept(metadata =>
            {
                metadata.Add("x-api-key", apiKey);
                return metadata;
            });
        }

        Client = new DatumCompute.DatumComputeClient(invoker);
    }

    /// <summary>
    /// Cancels one or all active queries on the specified target session.
    /// Requires the calling session to have admin privileges.
    /// </summary>
    /// <param name="sessionId">The admin session issuing the kill command.</param>
    /// <param name="targetSessionId">The session whose query should be cancelled.</param>
    /// <param name="queryId">
    /// Optional query identifier. When provided, only the specified query is
    /// cancelled. When <see langword="null"/> or empty, all active queries on
    /// the target session are cancelled.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for this RPC call.</param>
    /// <returns>The server's confirmation message.</returns>
    public async Task<string> CancelQueryAsync(
        string sessionId,
        string targetSessionId,
        string? queryId = null,
        CancellationToken cancellationToken = default)
    {
        KillQueryRequest request = new()
        {
            SessionId = sessionId,
            TargetSessionId = targetSessionId,
        };

        if (!string.IsNullOrEmpty(queryId))
        {
            request.QueryId = queryId;
        }

        KillQueryResponse response = await Client.KillQueryAsync(
            request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Message;
    }

    /// <summary>
    /// Cancels one or all active queries on the caller's own session. The session
    /// remains open and can run new queries immediately.
    /// </summary>
    /// <param name="sessionId">The session whose active query should be cancelled.</param>
    /// <param name="queryId">
    /// Optional query identifier. When provided, only the specified query is
    /// cancelled. When <see langword="null"/> or empty, all active queries on
    /// the session are cancelled.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for this RPC call.</param>
    /// <returns>The server's confirmation message.</returns>
    public async Task<string> CancelActiveQueryAsync(
        string sessionId,
        string? queryId = null,
        CancellationToken cancellationToken = default)
    {
        CancelQueryRequest request = new()
        {
            SessionId = sessionId,
        };

        if (!string.IsNullOrEmpty(queryId))
        {
            request.QueryId = queryId;
        }

        CancelQueryResponse response = await Client.CancelQueryAsync(
            request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Message;
    }

    /// <summary>
    /// Returns the currently executing queries on the specified session.
    /// </summary>
    /// <param name="sessionId">The session to list active queries for.</param>
    /// <param name="cancellationToken">Cancellation token for this RPC call.</param>
    /// <returns>The list of active queries with their identifiers, SQL, and start times.</returns>
    public async Task<ListActiveQueriesResponse> ListActiveQueriesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ListActiveQueriesRequest request = new()
        {
            SessionId = sessionId,
        };

        return await Client.ListActiveQueriesAsync(
            request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns usage statistics for the specified session.
    /// </summary>
    /// <param name="sessionId">The session to query usage for.</param>
    /// <param name="cancellationToken">Cancellation token for this RPC call.</param>
    /// <returns>The usage response containing accumulated Query Units and query count.</returns>
    public async Task<GetUsageResponse> GetUsageAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        GetUsageRequest request = new()
        {
            SessionId = sessionId,
        };

        return await Client.GetUsageAsync(
            request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channel.Dispose();
    }
}
