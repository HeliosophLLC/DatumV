using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.LlamaSharp;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Statistics;
using Heliosoph.DatumV.Ingestion;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering core Heliosoph.DatumV services
/// in an <see cref="IServiceCollection"/>.
/// </summary>
public static class DatumVServiceExtensions
{
    /// <summary>
    /// Registers core Heliosoph.DatumV services: <see cref="FormatRegistry"/>,
    /// <see cref="FunctionRegistry"/>, <see cref="PoolBacking"/>, and the
    /// inference layer (<see cref="IInferenceDispatcher"/> with the ORT
    /// and LlamaSharp backends registered).
    /// </summary>
    public static IServiceCollection AddDatumV(this IServiceCollection services)
    {
        services.AddSingleton<PoolBacking>();
        services.AddTransient<FormatRegistry>();
        services.AddTransient<StatisticsCollector>();
        services.AddTransient<SchemaDetector>();
        services.AddSingleton<FunctionRegistry>(_ => FunctionRegistry.CreateDefault());
        services.AddTransient(provider =>
        {
            PoolBacking backing = provider.GetRequiredService<PoolBacking>();

            return new Pool(backing);
        });

        // Inference: one dispatcher per process (the dispatcher caches
        // per-bundle session handles + cooperates with the residency
        // manager on VRAM accounting; multiple dispatchers fighting over
        // the same backends would race). Two backends today: ORT for
        // tensor-graph models (.onnx) and LlamaSharp for GGUF text
        // generation (.gguf). Each backend's Inspect() rejects extensions
        // it doesn't handle, so dispatch routes by file type without an
        // explicit PreferredBackends list. OpenVINO etc. fold in later
        // when their IInferenceBackend implementations land.
        services.AddSingleton<IInferenceDispatcher>(provider =>
        {
            ILogger<InferenceDispatcher> logger = provider.GetService<ILogger<InferenceDispatcher>>()
                ?? NullLogger<InferenceDispatcher>.Instance;
            return new InferenceDispatcher(
                [new OnnxRuntimeBackend(), new LlamaSharpBackend()],
                logger);
        });

        return services;
    }
}
