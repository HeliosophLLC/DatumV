using DatumIngest.Compute.Services;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="ApiKeyInterceptor"/> API key authentication.
/// </summary>
public sealed class ApiKeyInterceptorTests
{
    private const string ValidKey = "test-api-key-123";

    private readonly ApiKeyInterceptor _interceptor = new(
        Options.Create(new ApiKeyOptions { Key = ValidKey }));

    // ─────────────────── Unary calls ───────────────────

    /// <summary>
    /// Unary call with valid API key passes through to the continuation.
    /// </summary>
    [Fact]
    public async Task UnaryServerHandler_ValidKey_PassesThrough()
    {
        bool continuationCalled = false;
        TestCallContext context = TestCallContext.CreateWithApiKey(ValidKey);

        await _interceptor.UnaryServerHandler(
            "request",
            context,
            (request, ctx) =>
            {
                continuationCalled = true;
                return Task.FromResult("response");
            });

        Assert.True(continuationCalled);
    }

    /// <summary>
    /// Unary call without API key throws Unauthenticated.
    /// </summary>
    [Fact]
    public async Task UnaryServerHandler_MissingKey_ThrowsUnauthenticated()
    {
        TestCallContext context = TestCallContext.CreateWithoutApiKey();

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _interceptor.UnaryServerHandler(
                "request",
                context,
                (request, ctx) => Task.FromResult("response")));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.Contains("API key is required", exception.Status.Detail);
    }

    /// <summary>
    /// Unary call with invalid API key throws Unauthenticated.
    /// </summary>
    [Fact]
    public async Task UnaryServerHandler_InvalidKey_ThrowsUnauthenticated()
    {
        TestCallContext context = TestCallContext.CreateWithApiKey("wrong-key");

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _interceptor.UnaryServerHandler(
                "request",
                context,
                (request, ctx) => Task.FromResult("response")));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        Assert.Contains("Invalid API key", exception.Status.Detail);
    }

    // ─────────────────── Streaming calls ───────────────────

    /// <summary>
    /// Server streaming call with valid API key passes through.
    /// </summary>
    [Fact]
    public async Task ServerStreamingHandler_ValidKey_PassesThrough()
    {
        bool continuationCalled = false;
        TestCallContext context = TestCallContext.CreateWithApiKey(ValidKey);

        await _interceptor.ServerStreamingServerHandler(
            "request",
            new NoOpStreamWriter<string>(),
            context,
            (request, stream, ctx) =>
            {
                continuationCalled = true;
                return Task.CompletedTask;
            });

        Assert.True(continuationCalled);
    }

    /// <summary>
    /// Server streaming call without API key throws Unauthenticated.
    /// </summary>
    [Fact]
    public async Task ServerStreamingHandler_MissingKey_ThrowsUnauthenticated()
    {
        TestCallContext context = TestCallContext.CreateWithoutApiKey();

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _interceptor.ServerStreamingServerHandler(
                "request",
                new NoOpStreamWriter<string>(),
                context,
                (request, stream, ctx) => Task.CompletedTask));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    /// <summary>
    /// Server streaming call with invalid API key throws Unauthenticated.
    /// </summary>
    [Fact]
    public async Task ServerStreamingHandler_InvalidKey_ThrowsUnauthenticated()
    {
        TestCallContext context = TestCallContext.CreateWithApiKey("wrong-key");

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _interceptor.ServerStreamingServerHandler(
                "request",
                new NoOpStreamWriter<string>(),
                context,
                (request, stream, ctx) => Task.CompletedTask));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    /// <summary>
    /// API key comparison is case-sensitive.
    /// </summary>
    [Fact]
    public async Task UnaryServerHandler_CaseMismatch_ThrowsUnauthenticated()
    {
        TestCallContext context = TestCallContext.CreateWithApiKey(ValidKey.ToUpperInvariant());

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _interceptor.UnaryServerHandler(
                "request",
                context,
                (request, ctx) => Task.FromResult("response")));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    /// <summary>
    /// Minimal <see cref="ServerCallContext"/> stub with configurable request headers.
    /// </summary>
    private sealed class TestCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders;
        private readonly Metadata _responseTrailers = new();
        private Status _status;
        private WriteOptions? _writeOptions;

        private TestCallContext(Metadata requestHeaders)
        {
            _requestHeaders = requestHeaders;
        }

        /// <summary>Creates a context with a valid API key header.</summary>
        public static TestCallContext CreateWithApiKey(string apiKey)
        {
            Metadata headers = new() { { "x-api-key", apiKey } };
            return new TestCallContext(headers);
        }

        /// <summary>Creates a context without an API key header.</summary>
        public static TestCallContext CreateWithoutApiKey() =>
            new(new Metadata());

        /// <inheritdoc/>
        protected override string MethodCore => "/test";

        /// <inheritdoc/>
        protected override string HostCore => "localhost";

        /// <inheritdoc/>
        protected override string PeerCore => "127.0.0.1";

        /// <inheritdoc/>
        protected override DateTime DeadlineCore => DateTime.MaxValue;

        /// <inheritdoc/>
        protected override Metadata RequestHeadersCore => _requestHeaders;

        /// <inheritdoc/>
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        /// <inheritdoc/>
        protected override Metadata ResponseTrailersCore => _responseTrailers;

        /// <inheritdoc/>
        protected override Status StatusCore { get => _status; set => _status = value; }

        /// <inheritdoc/>
        protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }

        /// <inheritdoc/>
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        /// <inheritdoc/>
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// No-op <see cref="IServerStreamWriter{T}"/> for testing streaming interceptor methods.
    /// </summary>
    private sealed class NoOpStreamWriter<T> : IServerStreamWriter<T>
    {
        /// <inheritdoc/>
        public WriteOptions? WriteOptions { get; set; }

        /// <inheritdoc/>
        public Task WriteAsync(T message) => Task.CompletedTask;
    }
}
