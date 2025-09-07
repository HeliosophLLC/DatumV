using DatumIngest.Compute.Services;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DatumIngest.Compute;

/// <summary>
/// Extension methods for registering the DatumIngest gRPC compute backend
/// in an ASP.NET application. Call <see cref="AddDatumCompute"/> on the
/// service collection and <see cref="MapDatumCompute"/> on the endpoint
/// route builder.
/// </summary>
public static class DatumComputeServiceExtensions
{
    /// <summary>
    /// Registers the DatumIngest compute backend services: gRPC service,
    /// session management, command dispatch, and optional API key authentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compute backend requires a <see cref="FunctionRegistry"/> and a
    /// <see cref="SessionManager"/>. If they are not already registered in the
    /// service collection, this method registers default singletons.
    /// </para>
    /// <para>
    /// To supply a custom <see cref="IDatasetStore"/> (for example, backed by
    /// blob storage or S3), register it as a singleton <em>before</em> calling
    /// this method:
    /// </para>
    /// <code>
    /// builder.Services.AddSingleton&lt;IDatasetStore&gt;(new MyBlobDatasetStore(...));
    /// builder.Services.AddDatumCompute(options =&gt; options.ApiKey = "secret");
    /// </code>
    /// <para>
    /// If no <see cref="IDatasetStore"/> is registered, the <see cref="SessionManager"/>
    /// runs in local-only mode (no remote dataset pulling).
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">
    /// Optional callback to configure <see cref="DatumComputeOptions"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDatumCompute(
        this IServiceCollection services,
        Action<DatumComputeOptions>? configure = null)
    {
        DatumComputeOptions options = new();
        configure?.Invoke(options);

        // Register the server-default governor built from configuration.
        services.TryAddSingleton(new QueryGovernor(
            options.QueryTimeoutSeconds,
            options.MaxOutputRows,
            options.ThrottleDelayMilliseconds,
            options.MaxQueryUnits,
            options.MemoryBudgetBytes,
            options.MaxConcurrentQueries));

        // Wire ApiKeyOptions from the new unified options object.
        services.Configure<ApiKeyOptions>(apiKeyOptions =>
        {
            apiKeyOptions.Key = options.ApiKey;
        });

        // Register the process-global parallelism budget that prevents thread pool
        // oversubscription when multiple queries spawn parallel operator workers.
        if (options.MaxParallelWorkers is int maxParallelWorkers)
        {
            services.TryAddSingleton(new ParallelismBudget(maxParallelWorkers));
        }

        // Register engine services only if the host has not already provided them.
        services.TryAddSingleton<FunctionRegistry>(_ => FunctionRegistry.CreateDefault());

        services.TryAddSingleton<SessionManager>(provider =>
        {
            FunctionRegistry functionRegistry = provider.GetRequiredService<FunctionRegistry>();
            IDatasetStore? store = provider.GetService<IDatasetStore>();
            return new SessionManager(functionRegistry, store);
        });

        services.TryAddSingleton<CommandDispatcher>(provider =>
        {
            SessionManager sessionManager = provider.GetRequiredService<SessionManager>();
            ParallelismBudget? parallelismBudget = provider.GetService<ParallelismBudget>();
            return new CommandDispatcher(sessionManager, parallelismBudget);
        });

        // Register the gRPC interceptor and service.
        services.AddSingleton<ApiKeyInterceptor>();

        services.AddGrpc(grpcOptions =>
        {
            grpcOptions.MaxReceiveMessageSize = options.MaxReceiveMessageSize;
            grpcOptions.MaxSendMessageSize = options.MaxSendMessageSize;

            if (!string.IsNullOrEmpty(options.ApiKey))
            {
                grpcOptions.Interceptors.Add<ApiKeyInterceptor>();
            }
        });

        return services;
    }

    /// <summary>
    /// Maps the DatumIngest gRPC compute service endpoint.
    /// Call this after <see cref="AddDatumCompute"/> during application startup.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (typically <c>app</c>).</param>
    /// <returns>The gRPC service endpoint convention builder for further configuration.</returns>
    public static GrpcServiceEndpointConventionBuilder MapDatumCompute(
        this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGrpcService<ComputeService>();
    }

    /// <summary>
    /// Registers <see cref="SessionExpiryTimer"/> as an <see cref="IHostedService"/> so
    /// that it starts and stops with the application lifetime. Only call this when
    /// SignalR (or another broker) manages client sessions and needs the grace-period
    /// sweep to reclaim sessions after transient disconnects.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="checkInterval">
    /// How often the sweep timer checks for expired sessions.
    /// Defaults to 10 seconds when <see langword="null"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSessionExpiryTimer(
        this IServiceCollection services,
        TimeSpan? checkInterval = null)
    {
        TimeSpan interval = checkInterval ?? TimeSpan.FromSeconds(10);

        services.AddSingleton<IHostedService>(provider =>
        {
            SessionManager sessionManager = provider.GetRequiredService<SessionManager>();
            return new SessionExpiryTimerHostedService(sessionManager, interval);
        });

        return services;
    }
}
