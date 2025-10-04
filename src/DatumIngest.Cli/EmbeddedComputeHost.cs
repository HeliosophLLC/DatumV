using System.Net;
using System.Net.Sockets;
using DatumIngest.Catalog;
using DatumIngest.Compute;
using DatumIngest.Compute.Grpc;
using DatumIngest.Server;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Cli;

/// <summary>
/// Hosts the DatumIngest gRPC compute server in-process on a Unix Domain
/// Socket that is not exposed to the network. The CLI uses this to route
/// queries through the same <see cref="Compute.Services.ComputeService"/> that
/// the standalone server uses, ensuring a single execution path.
/// </summary>
internal sealed class EmbeddedComputeHost : IAsyncDisposable
{
    /// <summary>
    /// The well-known dataset identifier used to create an embedded session
    /// whose catalog is supplied via DI rather than a remote dataset store.
    /// </summary>
    public const string EmbeddedDatasetId = "embedded";

    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;
    private readonly string _socketPath;

    /// <summary>
    /// Gets the gRPC client connected to the in-process server.
    /// </summary>
    public DatumCompute.DatumComputeClient Client { get; }

    private EmbeddedComputeHost(WebApplication app, GrpcChannel channel, DatumCompute.DatumComputeClient client, string socketPath)
    {
        _app = app;
        _channel = channel;
        _socketPath = socketPath;
        Client = client;
    }

    /// <summary>
    /// Starts an in-process gRPC server with the given catalog and returns a
    /// host whose <see cref="Client"/> is ready for RPC calls.
    /// </summary>
    /// <param name="catalog">
    /// Pre-built table catalog from CLI arguments. Injected into the server via
    /// <see cref="IDatasetStore"/> so that <c>CreateSession</c> uses it.
    /// </param>
    /// <param name="configure">Optional callback to configure compute options.</param>
    /// <returns>A started host. Dispose to stop the server and clean up.</returns>
    public static async Task<EmbeddedComputeHost> StartAsync(
        TableCatalog catalog,
        Action<DatumComputeOptions>? configure = null)
    {
        string socketPath = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.sock");

        // Clean up stale socket file from a previous crash.
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenUnixSocket(socketPath, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        // Prevent the embedded host from intercepting Ctrl+C — the interactive
        // shell (RadLine) handles console cancel signals itself.
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();

        // Register the IDatasetStore and catalog factory BEFORE AddDatumCompute
        // so TryAddSingleton in AddDatumCompute does not overwrite them.
        builder.Services.AddSingleton<IDatasetStore>(new EmbeddedDatasetStore());
        builder.Services.AddSingleton<Func<string, Task<TableCatalog>>>(_ => Task.FromResult(catalog));

        builder.Services.AddDatumCompute(opts =>
        {
            // In-process: always include full stack traces in error responses.
            opts.EnableDetailedErrors = true;
            configure?.Invoke(opts);
        });

        // Suppress console logging from the embedded server.
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();
        app.MapDatumCompute();

        await app.StartAsync().ConfigureAwait(false);

        // Create a gRPC channel that connects via UDS.
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });

        var client = new DatumCompute.DatumComputeClient(channel);

        return new EmbeddedComputeHost(app, channel, client, socketPath);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
            // Best-effort cleanup; the OS will reclaim temp files.
        }
    }

    /// <summary>
    /// No-op host lifetime that does not register a Ctrl+C handler.
    /// Replaces the default <c>ConsoleLifetime</c> so the interactive
    /// shell retains control over console cancel signals.
    /// </summary>
    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A no-op <see cref="IDatasetStore"/> for embedded (in-process) hosting.
    /// The catalog is pre-built by the CLI and injected via a custom catalog
    /// factory, so <see cref="PullAsync"/> returns a sentinel path that is
    /// never read by the factory.
    /// </summary>
    private sealed class EmbeddedDatasetStore : IDatasetStore
    {
        /// <summary>
        /// The well-known dataset identifier used by the CLI to create an
        /// embedded session whose catalog is supplied by the DI container.
        /// </summary>
        public const string EmbeddedDatasetId = "embedded";

        public Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<string> PullAsync(string datasetId, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task EvictAsync(string datasetId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
