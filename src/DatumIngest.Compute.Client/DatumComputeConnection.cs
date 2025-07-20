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
    /// Cancels the active query on the specified target session.
    /// Requires the calling session to have admin privileges.
    /// </summary>
    /// <param name="sessionId">The admin session issuing the kill command.</param>
    /// <param name="targetSessionId">The session whose query should be cancelled.</param>
    /// <param name="cancellationToken">Cancellation token for this RPC call.</param>
    /// <returns>The server's confirmation message.</returns>
    public async Task<string> CancelQueryAsync(
        string sessionId,
        string targetSessionId,
        CancellationToken cancellationToken = default)
    {
        KillQueryRequest request = new()
        {
            SessionId = sessionId,
            TargetSessionId = targetSessionId,
        };

        KillQueryResponse response = await Client.KillQueryAsync(
            request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return response.Message;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _channel.Dispose();
    }
}
