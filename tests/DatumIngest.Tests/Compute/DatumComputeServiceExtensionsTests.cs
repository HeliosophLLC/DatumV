using DatumIngest.Compute;
using DatumIngest.Compute.Services;
using DatumIngest.Functions;
using DatumIngest.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="DatumComputeServiceExtensions"/> DI registration.
/// </summary>
public sealed class DatumComputeServiceExtensionsTests
{
    /// <summary>
    /// AddDatumCompute registers all required engine services.
    /// </summary>
    [Fact]
    public void AddDatumCompute_RegistersEngineServices()
    {
        ServiceCollection services = new();
        services.AddDatumCompute(options => options.ApiKey = "test-key");

        ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<FunctionRegistry>());
        Assert.NotNull(provider.GetService<SessionManager>());
        Assert.NotNull(provider.GetService<CommandDispatcher>());
    }

    /// <summary>
    /// AddDatumCompute configures the API key option correctly.
    /// </summary>
    [Fact]
    public void AddDatumCompute_ConfiguresApiKeyOptions()
    {
        ServiceCollection services = new();
        services.AddDatumCompute(options => options.ApiKey = "my-secret");

        ServiceProvider provider = services.BuildServiceProvider();

        IOptions<ApiKeyOptions> apiKeyOptions = provider.GetRequiredService<IOptions<ApiKeyOptions>>();
        Assert.Equal("my-secret", apiKeyOptions.Value.Key);
    }

    /// <summary>
    /// AddDatumCompute without configure callback still registers services.
    /// </summary>
    [Fact]
    public void AddDatumCompute_NoConfigure_RegistersDefaults()
    {
        ServiceCollection services = new();
        services.AddDatumCompute();

        ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<SessionManager>());
    }

    /// <summary>
    /// Host-provided IDatasetStore is picked up by SessionManager.
    /// </summary>
    [Fact]
    public void AddDatumCompute_CustomDatasetStore_IsResolved()
    {
        ServiceCollection services = new();
        TestDatasetStore store = new();
        services.AddSingleton<IDatasetStore>(store);
        services.AddDatumCompute();

        ServiceProvider provider = services.BuildServiceProvider();

        // SessionManager should have been created with the custom store.
        Assert.NotNull(provider.GetService<SessionManager>());
    }

    /// <summary>
    /// Host-provided FunctionRegistry is not overwritten by AddDatumCompute.
    /// </summary>
    [Fact]
    public void AddDatumCompute_CustomFunctionRegistry_IsPreserved()
    {
        ServiceCollection services = new();
        FunctionRegistry customRegistry = FunctionRegistry.CreateDefault();
        services.AddSingleton(customRegistry);
        services.AddDatumCompute();

        ServiceProvider provider = services.BuildServiceProvider();

        FunctionRegistry resolved = provider.GetRequiredService<FunctionRegistry>();
        Assert.Same(customRegistry, resolved);
    }

    /// <summary>
    /// Custom message size limits are respected.
    /// </summary>
    [Fact]
    public void AddDatumCompute_CustomMessageSizes_AreApplied()
    {
        ServiceCollection services = new();
        DatumComputeOptions capturedOptions = new();

        services.AddDatumCompute(options =>
        {
            options.MaxReceiveMessageSize = 128 * 1024 * 1024;
            options.MaxSendMessageSize = 32 * 1024 * 1024;
            capturedOptions = options;
        });

        Assert.Equal(128 * 1024 * 1024, capturedOptions.MaxReceiveMessageSize);
        Assert.Equal(32 * 1024 * 1024, capturedOptions.MaxSendMessageSize);
    }

    /// <summary>
    /// Minimal test <see cref="IDatasetStore"/> implementation.
    /// </summary>
    private sealed class TestDatasetStore : IDatasetStore
    {
        /// <inheritdoc/>
        public Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        /// <inheritdoc/>
        public Task<string> PullAsync(string datasetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        public Task EvictAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
