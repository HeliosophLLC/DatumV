namespace DatumIngest.Models.Llama;

/// <summary>
/// Bundles the chat-template format string and stop-sequence vocabulary for a
/// specific instruction-tuned LLM. Different model families (Llama 3.1, Phi-3,
/// Mistral, Qwen, …) use different header tokens to delimit user / assistant
/// turns; passing the wrong one to a given GGUF produces low-quality output
/// even though tokenisation succeeds. Centralising the template here lets a
/// single <see cref="LlamaModel"/> implementation serve every backend by
/// taking the template as a constructor parameter.
/// </summary>
/// <param name="Format">
/// Wraps a single user message in the template. The result is fed directly
/// to <see cref="LLama.StatelessExecutor.InferAsync(string, LLama.Abstractions.IInferenceParams, System.Threading.CancellationToken)"/>;
/// no leading <c>&lt;|begin_of_text|&gt;</c> / BOS — llama.cpp prepends it
/// based on the model's tokenizer config (<c>add_bos_token</c>).
/// </param>
/// <param name="StopSequences">
/// Anti-prompts that terminate generation when the model emits them. The
/// model may emit one of these as its final token; <see cref="StripTrailingStop"/>
/// scrubs them from the raw output before returning.
/// </param>
public sealed record LlamaChatTemplate(
    Func<string, string> Format,
    IReadOnlyList<string> StopSequences)
{
    /// <summary>
    /// Llama 3.1 / 3.2 Instruct template. Header IDs delimit roles; <c>&lt;|eot_id|&gt;</c>
    /// terminates the assistant turn. We deliberately omit the leading
    /// <c>&lt;|begin_of_text|&gt;</c> token — llama.cpp's tokenizer auto-adds
    /// the BOS because the model's metadata sets <c>add_bos_token = true</c>;
    /// including it here produces a double-BOS prompt that degrades output.
    /// </summary>
    public static readonly LlamaChatTemplate Llama31 = new(
        Format: msg =>
            "<|start_header_id|>user<|end_header_id|>\n\n" +
            msg +
            "<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n",
        StopSequences: ["<|eot_id|>"]);

    /// <summary>
    /// Microsoft Phi-3 / Phi-3.5 Instruct template. Uses <c>&lt;|user|&gt;</c>
    /// and <c>&lt;|assistant|&gt;</c> role markers with <c>&lt;|end|&gt;</c>
    /// terminating each turn.
    /// </summary>
    public static readonly LlamaChatTemplate Phi3 = new(
        Format: msg =>
            "<|user|>\n" + msg + "<|end|>\n<|assistant|>\n",
        StopSequences: ["<|end|>", "<|endoftext|>"]);

    /// <summary>
    /// Strips a trailing stop sequence from <paramref name="raw"/>, if present,
    /// then trims surrounding whitespace. The stop sequence is supposed to be
    /// consumed by the executor's anti-prompt list, but llama.cpp's emit
    /// boundary occasionally yields the literal text alongside the stop —
    /// strip defensively to avoid leaking marker tokens into the output.
    /// </summary>
    public string StripTrailingStop(string raw)
    {
        foreach (string stop in StopSequences)
        {
            int idx = raw.IndexOf(stop, StringComparison.Ordinal);
            if (idx >= 0)
            {
                raw = raw[..idx];
                break;
            }
        }
        return raw.Trim();
    }
}
