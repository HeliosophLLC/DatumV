using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace DatumIngest.Tests;

/// <summary>
/// Base class for tests that need a DI service provider. Creates a fresh
/// <see cref="ServiceCollection"/> per test instance (xUnit creates a new
/// instance per test method), registers core services, and disposes the
/// provider on cleanup.
/// </summary>
/// <remarks>
/// Subclasses can override <see cref="ConfigureServices"/> to add
/// additional registrations before the provider is built.
/// </remarks>
public abstract class ServiceTestBase : IDisposable
{
    private ServiceProvider? _provider;

    /// <summary>
    /// The built service provider. Lazily constructed on first access
    /// so that <see cref="ConfigureServices"/> has a chance to run.
    /// </summary>
    protected ServiceProvider Services => _provider ??= BuildProvider();

    /// <summary>
    /// Override to add additional service registrations.
    /// Called once before the provider is built.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // Core services available to all tests.
        services.AddDatumIngest();
        services.AddFileFormats();

        ConfigureServices(services);

        return services.BuildServiceProvider();
    }

    /// <summary>Resolves a service from the test provider.</summary>
    protected T GetService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    /// <summary>
    /// Creates an empty <see cref="TableCatalog"/> backed by a <see cref="Pool"/>
    /// resolved from the test's DI container. Each test instance gets its own
    /// <see cref="PoolBacking"/> (ServiceCollection is per-test), so catalogs
    /// are isolated across tests without sharing pool state.
    /// </summary>
    protected TableCatalog CreateCatalog()
    {
        return new TableCatalog(GetService<Pool>());
    }

    /// <summary>
    /// Creates a <see cref="TableCatalog"/> pre-populated with
    /// <see cref="InMemoryTableProvider"/> entries for each supplied
    /// <c>(name, rows)</c> pair.
    /// </summary>
    protected TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = CreateCatalog();

        foreach ((string name, Row[] rows) in tables)
        {
            catalog.Add(new InMemoryTableProvider(GetService<Pool>(), name, rows));
        }

        return catalog;
    }

    protected InMemoryTableProvider CreateInMemoryProvider(string name, Row[] rows)
        => new(GetService<Pool>(), name, rows);

    public virtual void Dispose()
    {
        _provider?.Dispose();
        GC.SuppressFinalize(this);
    }
}
