namespace DatumIngest.Models.Llama;

/// <summary>
/// Bundles the chat-template formatting primitives and stop-sequence
/// vocabulary for a specific instruction-tuned LLM family. Different
/// families (Llama 3.1, Phi-3, Mistral, Qwen, …) use different header
/// tokens to delimit user / assistant turns; passing the wrong format
/// to a given GGUF produces low-quality output even though tokenisation
/// succeeds. Centralising the family here lets a single
/// <see cref="LlamaModel"/> implementation serve every backend.
/// </summary>
/// <param name="Open">
/// BOS / preamble emitted once at the start of the prompt. Empty for
/// most families — llama.cpp's tokenizer auto-prepends BOS based on the
/// model's metadata (<c>add_bos_token</c>) so duplicating it produces a
/// double-BOS prompt that degrades output. Kept as a field for symmetry
/// with the per-message and assistant-turn primitives, and to leave room
/// for future per-family preambles (system-prompt slots, in-context
/// examples, etc.).
/// </param>
/// <param name="WrapMessage">
/// Wraps a single message in the family's role-header / turn-end syntax.
/// Roles are drawn from <c>'user'</c>, <c>'assistant'</c>, <c>'system'</c>,
/// <c>'tool'</c> where the family supports them; families that don't
/// have a native slot for a given role may surface that limitation
/// (Mistral has no system role; Gemma renames <c>'assistant'</c> to
/// <c>'model'</c> internally; etc.). Implementations should keep
/// transformation logic minimal — out-of-vocabulary roles should throw a
/// clear error rather than silently produce malformed output.
/// </param>
/// <param name="AssistantTurn">
/// The suffix appended after the last historical message that prompts
/// the model to speak — e.g. Llama 3.1's
/// <c>&lt;|start_header_id|&gt;assistant&lt;|end_header_id|&gt;\n\n</c>.
/// Combined with <see cref="Open"/> + <see cref="WrapMessage"/>, this
/// covers the full turn-by-turn templating dance.
/// </param>
/// <param name="StopSequences">
/// Anti-prompts that terminate generation when the model emits them. The
/// model may emit one of these as its final token; <see cref="StripTrailingStop"/>
/// scrubs them from the raw output before returning.
/// </param>
public sealed record LlamaChatTemplate(
    string Open,
    Func<string, string, string> WrapMessage,
    string AssistantTurn,
    IReadOnlyList<string> StopSequences)
{
    /// <summary>
    /// Backwards-compatible single-message helper used by one-shot inference
    /// in <see cref="LlamaModel.InferStreamingAsync"/>. Wraps a single user
    /// turn and appends the assistant prompt — equivalent to the legacy
    /// <c>Format</c> field on the pre-refactor template shape.
    /// </summary>
    public string Format(string userMsg) => Open + WrapMessage("user", userMsg) + AssistantTurn;

    /// <summary>
    /// Looks up a built-in template by canonical name (case-insensitive).
    /// SQL <c>llama_generate</c> / <c>llama_chat</c> scalars accept the
    /// family identifier as a string argument and resolve through here so
    /// catalog installSql doesn't have to thread C# enum values through.
    /// Returns <see langword="null"/> when no template matches; callers
    /// surface that as an actionable error including the supported set.
    /// </summary>
    public static LlamaChatTemplate? TryByName(string name) =>
        name?.ToLowerInvariant() switch
        {
            "llama31" or "llama-3.1" or "llama3.1" => Llama31,
            "phi3" or "phi-3" or "phi3.5" or "phi-3.5" => Phi3,
            "zephyr" => Zephyr,
            "gemma" => Gemma,
            "chatml" => ChatML,
            "mistral" => Mistral,
            "granite" => Granite,
            _ => null,
        };

    /// <summary>
    /// Comma-separated list of supported template names for use in error
    /// diagnostics. Kept in sync with <see cref="TryByName"/>'s switch.
    /// </summary>
    public const string SupportedTemplateNames =
        "llama31, phi3, zephyr, gemma, chatml, mistral, granite";

    // ─── Per-family templates ─────────────────────────────────────────────────
    //
    // Each family below is encoded as three primitives — Open, WrapMessage,
    // AssistantTurn — plus the StopSequences list. WrapMessage takes (role,
    // content) and returns the chunk for that turn, including its trailing
    // turn-end token. Composing N messages is concatenation of their
    // WrapMessage outputs; opening + N composed messages + AssistantTurn
    // is the full prompt sent to the model.

    /// <summary>
    /// Llama 3.1 / 3.2 Instruct template. Header IDs delimit roles;
    /// <c>&lt;|eot_id|&gt;</c> terminates each turn. The leading
    /// <c>&lt;|begin_of_text|&gt;</c> token is omitted — llama.cpp's
    /// tokenizer auto-adds the BOS because the model's metadata sets
    /// <c>add_bos_token = true</c>; including it here produces a
    /// double-BOS prompt that degrades output.
    /// </summary>
    public static readonly LlamaChatTemplate Llama31 = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<|start_header_id|>" + Llama31Role(role) + "<|end_header_id|>\n\n" +
            content +
            "<|eot_id|>",
        AssistantTurn: "<|start_header_id|>assistant<|end_header_id|>\n\n",
        StopSequences: ["<|eot_id|>"]);

    private static string Llama31Role(string role) => role switch
    {
        "user" or "assistant" or "system" => role,
        // Llama 3.1's tool-call return role is `ipython`. Map the generic
        // `tool` to it so SQL authors don't have to know the family quirk.
        "tool" => "ipython",
        _ => throw new ArgumentException(
            $"Llama 3.1 template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// Microsoft Phi-3 / Phi-3.5 Instruct template. Uses <c>&lt;|user|&gt;</c>
    /// and <c>&lt;|assistant|&gt;</c> role markers with <c>&lt;|end|&gt;</c>
    /// terminating each turn.
    /// </summary>
    public static readonly LlamaChatTemplate Phi3 = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<|" + Phi3Role(role) + "|>\n" + content + "<|end|>\n",
        AssistantTurn: "<|assistant|>\n",
        StopSequences: ["<|end|>", "<|endoftext|>"]);

    private static string Phi3Role(string role) => role switch
    {
        "user" or "assistant" or "system" => role,
        // Phi-3 has no native tool slot; surface tool messages as a user
        // turn (caller can prefix the content if disambiguation matters).
        "tool" => "user",
        _ => throw new ArgumentException(
            $"Phi-3 template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// Zephyr / HuggingFaceH4 chat template. Used by TinyLlama-1.1B-Chat-v1.0
    /// (which fine-tuned on the same prompt format the Zephyr team
    /// established). <c>&lt;|user|&gt;</c> / <c>&lt;|assistant|&gt;</c> /
    /// <c>&lt;|system|&gt;</c> role markers; <c>&lt;/s&gt;</c> terminates
    /// each turn.
    /// </summary>
    public static readonly LlamaChatTemplate Zephyr = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<|" + ZephyrRole(role) + "|>\n" + content + "</s>\n",
        AssistantTurn: "<|assistant|>\n",
        StopSequences: ["</s>", "<|user|>"]);

    private static string ZephyrRole(string role) => role switch
    {
        "user" or "assistant" or "system" => role,
        "tool" => "user",
        _ => throw new ArgumentException(
            $"Zephyr template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// Google Gemma instruct template (Gemma / Gemma 2 / Gemma 3 share this
    /// shape). <c>&lt;start_of_turn&gt;</c> opens a turn,
    /// <c>&lt;end_of_turn&gt;</c> closes it. The role keyword is bare text
    /// after <c>start_of_turn</c> — <c>user</c> for input, <c>model</c>
    /// for output (Gemma is the odd one out: <c>model</c>, not
    /// <c>assistant</c>). SQL authors pass <c>'assistant'</c> and the
    /// template emits <c>model</c> internally so the family quirk doesn't
    /// leak into call sites. Gemma has no native system role — system
    /// messages get folded as <c>user</c> turns.
    /// </summary>
    public static readonly LlamaChatTemplate Gemma = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<start_of_turn>" + GemmaRole(role) + "\n" + content + "<end_of_turn>\n",
        AssistantTurn: "<start_of_turn>model\n",
        StopSequences: ["<end_of_turn>", "<eos>"]);

    private static string GemmaRole(string role) => role switch
    {
        "user" => "user",
        "assistant" => "model",
        // No native slot for either system or tool — surface as user.
        "system" or "tool" => "user",
        _ => throw new ArgumentException(
            $"Gemma template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// ChatML chat template — originated by OpenAI for early GPT-3.5/4
    /// chat completions, now used by Qwen 2 / 2.5 (including the Coder
    /// variants), Falcon3, and a swath of community fine-tunes.
    /// <c>&lt;|im_start|&gt;</c> opens a turn, <c>&lt;|im_end|&gt;</c>
    /// closes it; role keyword (<c>user</c> / <c>assistant</c> /
    /// <c>system</c> / <c>tool</c>) follows the start tag.
    /// </summary>
    public static readonly LlamaChatTemplate ChatML = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<|im_start|>" + ChatMLRole(role) + "\n" + content + "<|im_end|>\n",
        AssistantTurn: "<|im_start|>assistant\n",
        StopSequences: ["<|im_end|>", "<|endoftext|>"]);

    private static string ChatMLRole(string role) => role switch
    {
        "user" or "assistant" or "system" or "tool" => role,
        _ => throw new ArgumentException(
            $"ChatML template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// Mistral Instruct template (v0.1 / v0.2 / v0.3). Wraps user turns in
    /// <c>[INST] ... [/INST]</c> with a single trailing space inside the
    /// brackets — Mistral's reference tokenizer expects exactly that
    /// whitespace and skipping it produces noticeably worse output.
    /// Assistant turns immediately follow the <c>[/INST]</c>; consecutive
    /// turn pairs concatenate without a separator. The leading
    /// <c>&lt;s&gt;</c> is omitted — llama.cpp's tokenizer auto-adds BOS.
    /// </summary>
    /// <remarks>
    /// Mistral has no native system role; the convention is to prepend
    /// system text to the first user message inside the same
    /// <c>[INST]</c> block. Rather than silently fold the system message
    /// into the next user turn (which the template doesn't see), this
    /// implementation throws on <c>'system'</c>: the SQL author is
    /// expected to concatenate the system prompt into the first user
    /// content explicitly. This surfaces the family limitation rather
    /// than papering over it with non-obvious behaviour.
    /// </remarks>
    public static readonly LlamaChatTemplate Mistral = new(
        Open: string.Empty,
        WrapMessage: MistralWrap,
        AssistantTurn: " [/INST]",
        StopSequences: ["</s>", "[INST]"]);

    private static string MistralWrap(string role, string content) => role switch
    {
        "user" => "[INST] " + content + " [/INST]",
        "assistant" => content + "</s> ",
        "tool" => "[INST] " + content + " [/INST]",
        "system" => throw new ArgumentException(
            "Mistral has no native system role. Concatenate the system text " +
            "into the first user message before passing the messages array " +
            "to llama_chat()."),
        _ => throw new ArgumentException(
            $"Mistral template received unsupported role '{role}'. " +
            "Supported: user, assistant, tool."),
    };

    /// <summary>
    /// IBM Granite 3.x instruct template. Roles wrap in
    /// <c>&lt;|start_of_role|&gt;...&lt;|end_of_role|&gt;</c>; turn
    /// content terminates with <c>&lt;|end_of_text|&gt;</c>. Visually
    /// wordier than Llama or ChatML but the structure is the same: open
    /// role, write message, close turn, open next role.
    /// </summary>
    public static readonly LlamaChatTemplate Granite = new(
        Open: string.Empty,
        WrapMessage: (role, content) =>
            "<|start_of_role|>" + GraniteRole(role) + "<|end_of_role|>" + content + "<|end_of_text|>\n",
        AssistantTurn: "<|start_of_role|>assistant<|end_of_role|>",
        StopSequences: ["<|end_of_text|>", "<|start_of_role|>"]);

    private static string GraniteRole(string role) => role switch
    {
        "user" or "assistant" or "system" or "tool" => role,
        _ => throw new ArgumentException(
            $"Granite template received unsupported role '{role}'. " +
            "Supported: user, assistant, system, tool."),
    };

    /// <summary>
    /// Strips a trailing stop sequence from <paramref name="raw"/>, if
    /// present, then trims surrounding whitespace. The stop sequence is
    /// supposed to be consumed by the executor's anti-prompt list, but
    /// llama.cpp's emit boundary occasionally yields the literal text
    /// alongside the stop — strip defensively to avoid leaking marker
    /// tokens into the output.
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
