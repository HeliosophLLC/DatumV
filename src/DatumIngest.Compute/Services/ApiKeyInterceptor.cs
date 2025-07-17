using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace DatumIngest.Compute.Services;

/// <summary>
/// gRPC interceptor that enforces API key authentication on all calls.
/// The expected key is read from <see cref="ApiKeyOptions"/>.
/// </summary>
internal sealed class ApiKeyInterceptor : Interceptor
{
    private const string ApiKeyHeader = "x-api-key";

    private readonly string _expectedKey;

    /// <summary>
    /// Initializes the interceptor with the configured API key options.
    /// </summary>
    /// <param name="options">Options containing the expected API key.</param>
    public ApiKeyInterceptor(IOptions<ApiKeyOptions> options)
    {
        _expectedKey = options.Value.Key;
    }

    /// <inheritdoc />
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(request, context);
    }

    /// <inheritdoc />
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(request, responseStream, context);
    }

    /// <summary>
    /// Validates the API key from the request metadata.
    /// </summary>
    /// <param name="context">The gRPC call context containing request headers.</param>
    /// <exception cref="RpcException">Thrown when the API key is missing or invalid.</exception>
    private void ValidateApiKey(ServerCallContext context)
    {
        string? apiKey = context.RequestHeaders.GetValue(ApiKeyHeader);

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "API key is required. Set the 'x-api-key' metadata header."));
        }

        if (!string.Equals(apiKey, _expectedKey, StringComparison.Ordinal))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Invalid API key."));
        }
    }
}

/// <summary>
/// Configuration options for API key authentication.
/// </summary>
public sealed class ApiKeyOptions
{
    /// <summary>Gets or sets the expected API key value.</summary>
    public string Key { get; set; } = "";
}
