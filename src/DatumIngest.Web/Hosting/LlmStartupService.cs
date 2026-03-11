using DatumIngest.Catalog;
using DatumIngest.Models;
using DatumIngest.Models.Llama;
using DatumIngest.Web.Llm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.Hosting;

// IHostedService that:
//   1. Reads the ModelCatalog attached to the TableCatalog (via BuiltinModels
//      during DI construction).
//   2. Picks the largest LLM that fits the VRAM budget (ModelSelector).
//   3. Acquires a lease — this triggers the actual model load into VRAM.
//   4. Wraps the lease in LlamaLlmDriver and publishes it via LlmDriverHolder.
//
// The lease is held for the app's lifetime (released in StopAsync). v1's
// "one LLM resident, never evicted" simplification dodges the eviction-
// correctness work currently in flight on ModelResidencyManager.
internal sealed class LlmStartupService : IHostedService
{
    private readonly TableCatalog _tableCatalog;
    private readonly LlmDriverHolder _holder;
    private readonly ILogger<LlmStartupService> _logger;
    private LlamaLlmDriver? _driver;

    public LlmStartupService(
        TableCatalog tableCatalog,
        LlmDriverHolder holder,
        ILogger<LlmStartupService> logger)
    {
        _tableCatalog = tableCatalog;
        _holder = holder;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ModelCatalog? modelCatalog = _tableCatalog.Models;
        if (modelCatalog is null)
        {
            _logger.LogWarning(
                "No ModelCatalog attached to TableCatalog — chat surface will be unavailable. " +
                "Set WebHostOptions.RegisterBuiltinModels = true to attach the standard zoo.");
            return;
        }

        ModelSelection selection = ModelSelector.Select(modelCatalog);
        _logger.LogInformation(
            "Selected LLM '{Name}' ({Display}, ~{Bytes} estimated VRAM, usable budget {Budget}).",
            selection.Entry.Name,
            selection.Entry.DisplayName ?? selection.Entry.Name,
            FormatBytes(selection.EstimatedBytes),
            FormatBytes(selection.UsableBudgetBytes));

        ModelLease lease = await modelCatalog
            .AcquireAsync(selection.Entry.Name, cancellationToken)
            .ConfigureAwait(false);

        LlamaChatTemplate template = ResolveTemplate(selection.Entry.Name);
        _driver = new LlamaLlmDriver(lease, selection.Entry.Name, template);
        _holder.Set(_driver);

        _logger.LogInformation("LLM '{Name}' loaded and ready.", selection.Entry.Name);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _driver?.Dispose();
        _driver = null;
        return Task.CompletedTask;
    }

    // Map model-catalog name → chat-template family. When ModelCatalogEntry
    // gains a TemplateFamily field (see project_message_graph_design follow-ups),
    // replace this with a direct lookup.
    private static LlamaChatTemplate ResolveTemplate(string modelName)
    {
        if (modelName.StartsWith("llama", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Llama31;
        if (modelName.StartsWith("phi3", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Phi3;
        if (modelName.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.ChatML;
        if (modelName.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Mistral;
        if (modelName.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Gemma;
        if (modelName.StartsWith("granite", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Granite;
        if (modelName.StartsWith("tinyllama", StringComparison.OrdinalIgnoreCase)) return LlamaChatTemplate.Zephyr;
        // Fall back to Llama 3.1 if we don't recognise the family — better
        // than throwing during startup; the prompt may be slightly off-format
        // but the model still responds.
        return LlamaChatTemplate.Llama31;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes == long.MaxValue) return "unbounded";
        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;
        return bytes >= GiB ? $"{bytes / GiB:F2} GiB" : $"{bytes / MiB:F1} MiB";
    }
}
