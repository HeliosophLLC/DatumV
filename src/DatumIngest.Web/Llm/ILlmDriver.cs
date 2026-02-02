using DatumIngest.Models.Llama;

namespace DatumIngest.Web.Llm;

// Single-LLM driver. Wraps the loaded model behind a streaming-tokens API.
// One instance per app — selected at startup by ModelSelector, lease held
// for the process lifetime. When multi-conversation / cold-start mini-model
// arrives, this becomes a router rather than a single driver.
public interface ILlmDriver
{
    // Stable name of the model the driver is bound to — matches
    // ModelCatalogEntry.Name, used in messages.model for inspection.
    string ModelName { get; }

    // Chat template for prompt assembly. The agent uses Open / WrapMessage /
    // AssistantTurn to build prompts in C#; matches the family inferred from
    // the model name at driver construction.
    LlamaChatTemplate Template { get; }

    // Streams the model's response to `prompt`. Each yielded string is a
    // fragment of the response (typically one or a few tokens). Concatenate
    // to recover the full response. Cancellation honored between chunks.
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct);
}
