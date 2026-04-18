using System.Runtime.CompilerServices;
using DatumIngest.Functions;
using DatumIngest.Models;
using DatumIngest.Models.Llama;

namespace DatumIngest.Web.Llm;

// Wraps a ModelLease + IModel pair. The lease is held for the driver's
// lifetime — no per-request acquire/release. Disposing the driver releases
// the lease and the residency manager is free to evict the model.
internal sealed class LlamaLlmDriver : ILlmDriver, IDisposable
{
    private readonly ModelLease _lease;
    private bool _disposed;

    public string ModelName { get; }
    public LlamaChatTemplate Template { get; }

    public LlamaLlmDriver(ModelLease lease, string modelName, LlamaChatTemplate template)
    {
        _lease = lease;
        ModelName = modelName;
        Template = template;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // IModel.InferStreamingAsync takes per-row inputs + per-row
        // overrides. For an LLM there's one input (the prompt) and no
        // overrides (defaults from the catalog entry — temperature,
        // max_tokens — drive sampling).
        IReadOnlyList<ValueRef> inputs = [ValueRef.FromString(prompt)];
        IReadOnlyList<ValueRef> overrides = [];

        await foreach (ValueRef chunk in _lease.Model
            .InferStreamingAsync(inputs, overrides, ct)
            .ConfigureAwait(false))
        {
            yield return chunk.AsString();
        }
    }

    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // chars/4 with a floor of 1 — see ILlmDriver.CountTokens for why
        // this is a deliberate placeholder rather than the model's real
        // tokenizer.
        return Math.Max(1, text.Length / 4);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
    }
}
