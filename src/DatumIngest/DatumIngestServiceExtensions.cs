using DatumIngest.Pooling;
using DatumIngest.Functions;
using DatumIngest.Serialization;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering core DatumIngest services
/// in an <see cref="IServiceCollection"/>.
/// </summary>
public static class DatumIngestServiceExtensions
{
    /// <summary>
    /// Registers core DatumIngest services: <see cref="FormatRegistry"/>,
    /// <see cref="FunctionRegistry"/>, and <see cref="PoolBacking"/>.
    /// </summary>
    public static IServiceCollection AddDatumIngest(this IServiceCollection services)
    {
        services.AddSingleton<PoolBacking>();
        services.AddTransient<FormatRegistry>();
        services.AddSingleton<FunctionRegistry>(_ => FunctionRegistry.CreateDefault());
        services.AddTransient(provider =>
        {
            PoolBacking backing = provider.GetRequiredService<PoolBacking>();

            return new Pool(backing);
        });

        return services;
    }
}
