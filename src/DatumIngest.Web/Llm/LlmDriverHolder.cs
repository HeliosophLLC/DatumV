using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Models.Llama;
using Heliosoph.DatumV.Web.Hosting;
using Heliosoph.DatumV.Web.Settings;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.Web.Llm;

// Lazy single-flight loader for the singleton ILlmDriver. The first caller
// to GetAsync triggers ModelSelector.Select + ModelLease acquisition + driver
// construction; concurrent callers await the same task; the loaded driver is
// cached for the app's lifetime and disposed in DisposeAsync.
//
// Eager preload at startup was dropped in favour of load-on-first-chat: the
// LLM is a rarely-used surface here and was spending VRAM + startup time for
// nothing. The chat endpoint pays the load cost on the first user send.
//
// One-shot today: model swap at runtime is unsupported. When it lands, the
// cached task becomes a CompareAndSwap of (modelName, Task<ILlmDriver>).
internal sealed class LlmDriverHolder : IAsyncDisposable
{
    private readonly TableCatalog _tableCatalog;
    private readonly WebHostOptions _options;
    private readonly ILogger<LlmDriverHolder> _logger;
    private readonly object _gate = new();
    private Task<ILlmDriver>? _loadTask;
    private ILlmDriver? _loaded;

    public LlmDriverHolder(
        TableCatalog tableCatalog,
        WebHostOptions options,
        ILogger<LlmDriverHolder> logger)
    {
        _tableCatalog = tableCatalog;
        _options = options;
        _logger = logger;
    }

    public Task<ILlmDriver> GetAsync(CancellationToken cancellationToken)
    {
        Task<ILlmDriver> task;
        lock (_gate)
        {
            // On a previous load that faulted, drop the cached task so the
            // next caller retries from scratch instead of inheriting the
            // failure for the process lifetime.
            if (_loadTask is { IsCompletedSuccessfully: false, IsCompleted: true })
            {
                _loadTask = null;
            }
            // Load runs with CancellationToken.None: once started it runs to
            // completion regardless of any single caller's cancellation, so a
            // user who hits Cancel on their first chat doesn't tear down a
            // load that other callers are waiting on. Each caller's token
            // only gates their own wait via WaitAsync below.
            _loadTask ??= Task.Run(() => LoadAsync(), CancellationToken.None);
            task = _loadTask;
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ILlmDriver> LoadAsync()
    {
        ModelCatalog? modelCatalog = _tableCatalog.Models
            ?? throw new InvalidOperationException(
                "No ModelCatalog attached to TableCatalog — chat surface is unavailable. " +
                "Set WebHostOptions.RegisterBuiltinModels = true to attach the model subsystem via ModelHost.");

        // Read the user's pinned LLM at load time so settings → restart →
        // load picks up changes without a second redeploy hop. The setting
        // file path is the same one ISettingsService writes to; reading it
        // directly avoids the scoped-from-singleton plumbing.
        string? preferred = !string.IsNullOrWhiteSpace(_options.CatalogRootPath)
            ? StartupSettingsLoader.LoadDefaultLlm(_options.CatalogRootPath)
            : null;

        ModelSelection? maybeSelection = ModelSelector.TrySelect(modelCatalog, preferred);
        if (maybeSelection is null)
        {
            throw new NoLlmInstalledException(
                "No LLM is installed that fits the current VRAM budget. " +
                "Install an LLM from the Models tab to enable chat.");
        }

        ModelSelection selection = maybeSelection;
        _logger.LogInformation(
            "Selected LLM '{Name}' ({Display}, ~{Bytes} estimated VRAM, usable budget {Budget}).",
            selection.Entry.Name,
            selection.Entry.DisplayName ?? selection.Entry.Name,
            FormatBytes(selection.EstimatedBytes),
            FormatBytes(selection.UsableBudgetBytes));

        ModelLease lease = await modelCatalog
            .AcquireAsync(selection.Entry.Name, CancellationToken.None)
            .ConfigureAwait(false);

        LlamaChatTemplate template = ResolveTemplate(selection.Entry.Name);
        LlamaLlmDriver driver = new(lease, selection.Entry.Name, template);
        _loaded = driver;

        _logger.LogInformation("LLM '{Name}' loaded and ready.", selection.Entry.Name);
        return driver;
    }

    public ValueTask DisposeAsync()
    {
        if (_loaded is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _loaded = null;
        return ValueTask.CompletedTask;
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
        // than throwing during load; the prompt may be slightly off-format
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
