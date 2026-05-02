using System.Text;

using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.LlamaSharp;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models.Llama;

using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;

namespace DatumIngest.Functions.Scalar.Vector;

/// <summary>
/// One-shot LLM completion against a GGUF-backed session. The
/// <c>TextGenerator</c>-shaped scalar: takes a raw prompt string, wraps
/// it in the named chat template's single-user-turn shape, runs the
/// session, returns the assistant's response as one String.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Surface.</strong>
/// <code>
/// llama_generate(
///     session_alias  String,                          -- USING clause alias
///     prompt         String,                          -- user message
///     template       String     := 'llama31',         -- chat-template family
///     max_tokens     Int32      := 256,
///     temperature    Float32    := 0.7
/// ) RETURNS String
/// </code>
/// Per-CREATE-MODEL overrides flow through the body's declared
/// parameters — the catalog author writes
/// <c>RETURN llama_generate('s', prompt, max_tokens := max_tokens,
/// temperature := temperature)</c> and the engine validates the
/// declared parameters' <c>CHECK</c> clauses (e.g.
/// <c>CHECK BETWEEN 1 AND 4096</c>) before this scalar sees them.
/// </para>
/// <para>
/// <strong>Non-streaming.</strong> Returns the full response after
/// generation completes. Token-by-token streaming via CALL is a
/// separate dispatch path; in Slice 3 the SQL surface is
/// non-streaming for simplicity.
/// </para>
/// <para>
/// <strong>Validation.</strong> Built-in numeric checks (max_tokens
/// positive, temperature in [0, 2]) catch arguments that slipped
/// past the CREATE MODEL parameter <c>CHECK</c> clauses or are
/// being supplied directly from a non-checked call site.
/// </para>
/// </remarks>
public sealed class LlamaGenerateFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "llama_generate";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "LLM completion against a GGUF-backed session. Wraps the prompt in "
        + "the named chat template's single-user-turn shape and returns the "
        + "assistant's response as one String. Implements the "
        + "TextGenerator task contract.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session_alias", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("prompt",        DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("template",      DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar, IsOptional: true),
                new ParameterSpec("max_tokens",    DataKindMatcher.Exact(DataKind.Int32),  IsArray: ArrayMatch.Scalar, IsOptional: true),
                new ParameterSpec("temperature",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar, IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LlamaGenerateFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        int argLen = arguments.Length;
        if (argLen < 2 || argLen > 5)
        {
            throw new InvalidOperationException(
                $"llama_generate() expects 2-5 arguments (session_alias, prompt, "
                + $"[template], [max_tokens], [temperature]); got {argLen}.");
        }
        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                "llama_generate() is only callable from inside a CREATE MODEL body.");
        }

        string alias;
        string prompt;
        string templateName;
        int maxTokens;
        float temperature;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            alias        = args[0].AsString();
            prompt       = args[1].AsString();
            templateName = argLen > 2 && !args[2].IsNull ? args[2].AsString() : LlamaScalarDefaults.TemplateName;
            maxTokens    = argLen > 3 && !args[3].IsNull ? args[3].AsInt32()  : LlamaScalarDefaults.MaxTokens;
            temperature  = argLen > 4 && !args[4].IsNull ? args[4].ToFloat()  : LlamaScalarDefaults.Temperature;
        }

        LlamaScalarShared.ValidateMaxTokens(maxTokens, Name);
        LlamaScalarShared.ValidateTemperature(temperature, Name);

        LlamaChatTemplate template = LlamaScalarShared.ResolveTemplate(templateName, Name);
        LlamaSharpSession session = await LlamaScalarShared.ResolveLlamaSessionAsync(
            model.BoundSessions, alias, Name, cancellationToken).ConfigureAwait(false);

        // TextGenerator surface: one user turn, no system message. The
        // GGUF's native chat template handles role markers / special-token
        // tokenization — see GenerateAsync for why we don't hand-roll.
        (string Role, string Content)[] messages = [("user", prompt)];
        string completion = await LlamaScalarShared.GenerateAsync(
            session, template, messages, maxTokens, temperature, cancellationToken)
            .ConfigureAwait(false);

        return ValueRef.FromString(completion);
    }
}

/// <summary>
/// Multi-turn chat completion against a GGUF-backed session. The
/// <c>ChatCompleter</c>-shaped scalar: takes an
/// <c>Array&lt;ChatMessage&gt;</c> conversation, renders each message
/// through the named chat template's role-aware <c>WrapMessage</c>,
/// runs the session, returns the assistant's response.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Surface.</strong>
/// <code>
/// llama_chat(
///     session_alias  String,                          -- USING clause alias
///     messages       Array&lt;ChatMessage&gt;,              -- ordered conversation
///     template       String     := 'llama31',
///     max_tokens     Int32      := 256,
///     temperature    Float32    := 0.7
/// ) RETURNS String
/// </code>
/// </para>
/// <para>
/// <strong>Empty message list.</strong> Rejected with a clear error —
/// dispatching to the model with only the assistant-turn marker
/// produces meaningless output and is almost certainly a caller bug.
/// </para>
/// <para>
/// <strong>Role vocabulary.</strong> Each message's <c>role</c> field
/// is passed through to the named template's <c>WrapMessage</c>;
/// unsupported roles surface as the template's own
/// <see cref="ArgumentException"/> with the family's accepted set
/// (e.g. Mistral has no native system role).
/// </para>
/// </remarks>
public sealed class LlamaChatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "llama_chat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Multi-turn chat completion against a GGUF-backed session. Renders "
        + "an Array<ChatMessage> through the named chat template and returns "
        + "the assistant's response as one String. Implements the "
        + "ChatCompleter task contract.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session_alias", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("messages",      DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Array),
                new ParameterSpec("template",      DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar, IsOptional: true),
                new ParameterSpec("max_tokens",    DataKindMatcher.Exact(DataKind.Int32),  IsArray: ArrayMatch.Scalar, IsOptional: true),
                new ParameterSpec("temperature",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar, IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LlamaChatFunction>(argumentKinds);

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        int argLen = arguments.Length;
        if (argLen < 2 || argLen > 5)
        {
            throw new InvalidOperationException(
                $"llama_chat() expects 2-5 arguments (session_alias, messages, "
                + $"[template], [max_tokens], [temperature]); got {argLen}.");
        }
        if (frame.CurrentModel is not { } model)
        {
            throw new InvalidOperationException(
                "llama_chat() is only callable from inside a CREATE MODEL body.");
        }

        string alias;
        string templateName;
        int maxTokens;
        float temperature;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            alias        = args[0].AsString();
            templateName = argLen > 2 && !args[2].IsNull ? args[2].AsString() : LlamaScalarDefaults.TemplateName;
            maxTokens    = argLen > 3 && !args[3].IsNull ? args[3].AsInt32()  : LlamaScalarDefaults.MaxTokens;
            temperature  = argLen > 4 && !args[4].IsNull ? args[4].ToFloat()  : LlamaScalarDefaults.Temperature;
        }

        LlamaScalarShared.ValidateMaxTokens(maxTokens, Name);
        LlamaScalarShared.ValidateTemperature(temperature, Name);

        LlamaChatTemplate template = LlamaScalarShared.ResolveTemplate(templateName, Name);

        // Assemble the templated prompt from the messages array. Two-phase
        // extract — span-from-arguments can't survive the await on session
        // resolve, so collect (role, content) pairs first.
        List<(string Role, string Content)> messages;
        {
            ReadOnlySpan<ValueRef> args = arguments.Span;
            ValueRef messagesArg = args[1];
            if (messagesArg.IsNull)
            {
                throw new InvalidOperationException(
                    "llama_chat(): messages argument is NULL. Pass at least one ChatMessage.");
            }
            ReadOnlySpan<ValueRef> elements = messagesArg.GetArrayElements();
            if (elements.Length == 0)
            {
                throw new InvalidOperationException(
                    "llama_chat(): messages array is empty. At least one ChatMessage is required.");
            }
            messages = new List<(string, string)>(elements.Length);
            for (int i = 0; i < elements.Length; i++)
            {
                ValueRef element = elements[i];
                if (element.IsNull)
                {
                    throw new InvalidOperationException(
                        $"llama_chat(): messages[{i}] is NULL. ChatMessage rows must be non-null.");
                }
                ReadOnlySpan<ValueRef> fields = element.GetStructFields();
                if (fields.Length != 2)
                {
                    throw new InvalidOperationException(
                        $"llama_chat(): messages[{i}] must be ChatMessage(role: String, content: String) "
                        + $"— got struct with {fields.Length} field(s).");
                }
                if (fields[0].IsNull || fields[1].IsNull)
                {
                    throw new InvalidOperationException(
                        $"llama_chat(): messages[{i}] has NULL role or content. "
                        + "Both fields must be non-null strings.");
                }
                messages.Add((fields[0].AsString(), fields[1].AsString()));
            }
        }

        LlamaSharpSession session = await LlamaScalarShared.ResolveLlamaSessionAsync(
            model.BoundSessions, alias, Name, cancellationToken).ConfigureAwait(false);

        // Prompt construction is delegated to llama.cpp's native template
        // engine inside GenerateAsync — see the rationale on
        // LlamaSharpSession.Weights for why hand-rolled templates can't
        // be tokenized reliably on some GGUF quants. Pass the (role,
        // content) list and let the GGUF's embedded template format it
        // with correct special-token tokenization.
        string completion = await LlamaScalarShared.GenerateAsync(
            session, template, messages, maxTokens, temperature, cancellationToken)
            .ConfigureAwait(false);

        return ValueRef.FromString(completion);
    }
}

/// <summary>
/// Defaults shared by <see cref="LlamaGenerateFunction"/> and
/// <see cref="LlamaChatFunction"/> when a per-call argument is omitted
/// or NULL. Match the legacy <c>LlamaModel</c> constructor defaults so
/// SQL-defined LLMs behave identically to the C# registrations during
/// the migration.
/// </summary>
internal static class LlamaScalarDefaults
{
    public const string TemplateName = "llama31";
    public const int MaxTokens = 256;
    public const float Temperature = 0.7f;
}

/// <summary>
/// Helpers shared by the LLM-shaped scalars. Centralises template
/// resolution, session resolution + cast, argument validation, and the
/// generation loop so both <see cref="LlamaGenerateFunction"/> and
/// <see cref="LlamaChatFunction"/> stay shallow dispatch shims.
/// </summary>
internal static class LlamaScalarShared
{
    /// <summary>Maps a template-name string to a built-in template, or throws with the supported set.</summary>
    internal static LlamaChatTemplate ResolveTemplate(string name, string callerName)
    {
        LlamaChatTemplate? template = LlamaChatTemplate.TryByName(name);
        if (template is null)
        {
            throw new InvalidOperationException(
                $"{callerName}(): unknown chat template '{name}'. "
                + $"Supported: [{LlamaChatTemplate.SupportedTemplateNames}].");
        }
        return template;
    }

    /// <summary>
    /// Resolves <paramref name="alias"/> from <paramref name="bound"/> and
    /// casts to the GGUF-backed session type. Surfacing the cast failure
    /// here means the call site gets a precise diagnostic when an author
    /// accidentally aliases an ONNX file under what they declared as a
    /// llama_chat session.
    /// </summary>
    internal static async ValueTask<LlamaSharpSession> ResolveLlamaSessionAsync(
        Catalog.Registries.LazyModelSessions bound,
        string alias,
        string callerName,
        CancellationToken cancellationToken)
    {
        if (!bound.ContainsKey(alias))
        {
            throw new InvalidOperationException(
                $"{callerName}(): session alias '{alias}' is not bound. "
                + $"Available aliases: [{string.Join(", ", bound.Keys)}]. "
                + "Aliases come from the CREATE MODEL's USING clause (`USING 'path' AS alias`).");
        }

        IModelSession resolved = await bound.ResolveAsync(alias, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is not LlamaSharpSession llama)
        {
            throw new InvalidOperationException(
                $"{callerName}(): session alias '{alias}' is bound to a "
                + $"{resolved.Backend} session, but {callerName}() requires a "
                + ".gguf-backed LlamaSharp session. Use a different USING path "
                + "or call the backend-matching scalar (e.g. infer() for ONNX).");
        }
        return llama;
    }

    /// <summary>Sanity bound on <c>max_tokens</c>; safety net beneath any CHECK clauses the model declares.</summary>
    internal static void ValidateMaxTokens(int value, string callerName)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"{callerName}(): max_tokens must be positive; got {value}.");
        }
    }

    /// <summary>Sanity bound on <c>temperature</c>; safety net beneath any CHECK clauses the model declares.</summary>
    internal static void ValidateTemperature(float value, string callerName)
    {
        if (!float.IsFinite(value) || value < 0f || value > 2f)
        {
            throw new InvalidOperationException(
                $"{callerName}(): temperature must be a finite value in [0, 2]; got {value}.");
        }
    }

    /// <summary>
    /// Runs the executor against the templated prompt and collects the full
    /// response into one string. Mirrors the streaming + holdback +
    /// stop-strip logic from <see cref="LlamaModel.InferStreamingAsync"/>
    /// so both registration paths produce identical text for identical
    /// prompts.
    /// </summary>
    internal static async ValueTask<string> GenerateAsync(
        LlamaSharpSession session,
        LlamaChatTemplate template,
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens,
        float temperature,
        CancellationToken cancellationToken)
    {
        // Prompt construction via llama.cpp's native chat-template engine.
        // This is the same path StatelessExecutor.ApplyTemplate=true uses
        // internally; we call it ourselves so multi-turn message lists
        // (llama_chat) and single-turn prompts (llama_generate) share
        // one tokenization path. Hand-rolling the templated string from
        // the LlamaChatTemplate primitives produced byte-correct output
        // but Phi-3.5's bartowski quant tokenized the role markers as
        // ordinary text, causing the model to treat the prompt as a
        // document-continuation rather than a chat turn — visible as the
        // "## Instruction 2 / Solution 2" hallucination after a short
        // valid response. Going through LLamaTemplate + ToModelPrompt
        // guarantees special tokens (<|user|>, <|end|>, <|assistant|>,
        // <|eot_id|>, etc.) tokenize as their actual ids per the GGUF's
        // embedded chat template.
        LLamaTemplate llamaTemplate = new(session.Weights.NativeHandle)
        {
            AddAssistant = true,
        };
        foreach ((string role, string content) in messages)
        {
            llamaTemplate.Add(role, content);
        }
        string templatedPrompt = PromptTemplateTransformer.ToModelPrompt(llamaTemplate);

        InferenceParams inferenceParams = new()
        {
            MaxTokens = maxTokens,
            // Stop sequences from the caller-named template remain a
            // safety net beneath llama.cpp's native IsEndOfGeneration
            // check (which terminates on EOG-flagged tokens). They cover
            // the rare quant where a turn-end marker isn't flagged as EOG
            // but does appear as text in the stream.
            AntiPrompts = [.. template.StopSequences],
            // Surfaces special tokens as text in the stream so AntiPrompts
            // can match them. With llama.cpp now driving the prompt
            // tokenization correctly, the model should emit its
            // turn-end marker as a real special token id and
            // IsEndOfGeneration will catch it — this flag is the
            // belt-and-suspenders fallback for quants whose EOG metadata
            // is incomplete.
            DecodeSpecialTokens = true,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                // Fresh seed per call so identical prompts can still produce
                // different outputs across calls — matches the legacy
                // LlamaModel behaviour where each row dispatches with its
                // own seed.
                Seed = (uint)Random.Shared.Next(),
            },
        };

        StringBuilder buffer = new();
        await foreach (string token in session.Executor
            .InferAsync(templatedPrompt, inferenceParams, cancellationToken)
            .ConfigureAwait(false))
        {
            buffer.Append(token);

            // Mid-stream stop sequence — terminate immediately rather than
            // wait for the executor's anti-prompt list, so a stop token
            // that appears earlier than expected doesn't keep generating.
            string snapshot = buffer.ToString();
            int stopAt = -1;
            foreach (string stop in template.StopSequences)
            {
                int idx = snapshot.IndexOf(stop, StringComparison.Ordinal);
                if (idx >= 0 && (stopAt < 0 || idx < stopAt))
                {
                    stopAt = idx;
                }
            }
            if (stopAt >= 0)
            {
                return snapshot[..stopAt].TrimEnd();
            }
        }

        // Generation ended without hitting a stop (max_tokens reached or
        // executor stream exhausted). Strip a trailing stop defensively
        // and trim whitespace — same shape the legacy LlamaModel returns.
        return template.StripTrailingStop(buffer.ToString());
    }
}
