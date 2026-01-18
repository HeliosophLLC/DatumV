using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models.Llama;

namespace DatumIngest.Functions.Templates;

/// <summary>
/// Scalar SQL functions exposing per-family chat-template primitives so a
/// multi-turn LLM prompt can be assembled in plain SQL without aggregate
/// or model-aware machinery in the engine. Each family registers three
/// functions:
///
///   <c>templates.{family}_open()</c>            → preamble text (often empty)
///   <c>templates.{family}_msg(role, content)</c> → wrapped per-message chunk
///   <c>templates.{family}_assistant_turn()</c>  → assistant-prompt suffix
///
/// Composed in SQL alongside <c>string_agg</c> they produce the full
/// templated prompt for a conversation; pair with the <c>templated</c>
/// override on a Llama model to bypass its per-call <c>Format</c> wrapper:
///
/// <code>
/// SELECT models.llama_3_2(
///   templates.llama31_open()
///   || string_agg(templates.llama31_msg(role, content)) WITHIN GROUP (ORDER BY turn_index)
///   || templates.llama31_assistant_turn(),
///   NULL, NULL, true)              -- temperature, max_tokens, templated
/// FROM messages WHERE conversation_id = @conv_id;
/// </code>
///
/// Pure (CSE-eligible): the same family / role / content always produces
/// the same string. The model invocation isn't pure, but the templating
/// helpers themselves are.
/// </summary>
internal static class ChatTemplateFunctions
{
    /// <summary>
    /// Per-family entries used by <see cref="RegisterAll"/>. The string
    /// key becomes the namespace prefix (e.g. <c>"llama31"</c> →
    /// <c>templates.llama31_open</c> / <c>_msg</c> / <c>_assistant_turn</c>).
    /// Mirrors the public templates in <see cref="LlamaChatTemplate"/>.
    /// </summary>
    private static readonly IReadOnlyList<(string Key, LlamaChatTemplate Template)> Families =
    [
        ("llama31", LlamaChatTemplate.Llama31),
        ("phi3",    LlamaChatTemplate.Phi3),
        ("zephyr",  LlamaChatTemplate.Zephyr),
        ("gemma",   LlamaChatTemplate.Gemma),
        ("chatml",  LlamaChatTemplate.ChatML),
        ("mistral", LlamaChatTemplate.Mistral),
        ("granite", LlamaChatTemplate.Granite),
    ];

    /// <summary>
    /// Registers all <c>templates.X_*</c> scalar functions for every
    /// supported family. Idempotent at the registration call level — re-
    /// invoking against the same registry would throw on the duplicate
    /// name; callers shouldn't.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        foreach ((string key, LlamaChatTemplate template) in Families)
        {
            string openName = $"templates.{key}_open";
            string msgName = $"templates.{key}_msg";
            string asstName = $"templates.{key}_assistant_turn";

            registry.RegisterScalarInstance(
                openName,
                new ChatTemplateOpenFunction(openName, template),
                descriptor: BuildOpenDescriptor(openName, key));

            registry.RegisterScalarInstance(
                msgName,
                new ChatTemplateMessageFunction(msgName, template),
                descriptor: BuildMessageDescriptor(msgName, key));

            registry.RegisterScalarInstance(
                asstName,
                new ChatTemplateAssistantTurnFunction(asstName, template),
                descriptor: BuildAssistantTurnDescriptor(asstName, key));
        }
    }

    // ───── Descriptors ────────────────────────────────────────────────────────
    //
    // Hand-built FunctionDescriptors because RegisterScalarInstance doesn't
    // read static-abstract metadata (the same instance class powers many
    // names). Categorised under String — these functions produce template
    // text, and grouping them with concat/upper/lower in completion makes
    // sense for users assembling prompts.

    private static FunctionDescriptor BuildOpenDescriptor(string name, string family) => new(
        PrimaryName: name,
        Aliases: Array.Empty<string>(),
        Category: FunctionCategory.String,
        Description:
            $"Returns the chat-template preamble (BOS / system header) for the {family} " +
            "family. Often empty because llama.cpp auto-prepends BOS based on the " +
            "model's metadata; kept as a function for symmetry and future use.",
        Signatures:
        [
            new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        ]);

    private static FunctionDescriptor BuildMessageDescriptor(string name, string family) => new(
        PrimaryName: name,
        Aliases: Array.Empty<string>(),
        Category: FunctionCategory.String,
        Description:
            $"Wraps one message in the {family} family's role-header / turn-end " +
            "syntax. Roles: 'user', 'assistant', 'system', 'tool' where the family " +
            "supports them; unsupported roles raise an error rather than silently " +
            "produce malformed output.",
        Signatures:
        [
            new FunctionSignatureVariant(
                Parameters:
                [
                    new ParameterSpec("role",    DataKindMatcher.Exact(DataKind.String)),
                    new ParameterSpec("content", DataKindMatcher.Exact(DataKind.String)),
                ],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        ]);

    private static FunctionDescriptor BuildAssistantTurnDescriptor(string name, string family) => new(
        PrimaryName: name,
        Aliases: Array.Empty<string>(),
        Category: FunctionCategory.String,
        Description:
            $"Returns the {family} family's assistant-turn suffix — the trailing " +
            "header that prompts the model to speak after the conversation history.",
        Signatures:
        [
            new FunctionSignatureVariant(
                Parameters: [],
                VariadicTrailing: null,
                ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        ]);
}

// ───── Function classes ───────────────────────────────────────────────────────

/// <summary>
/// Returns <see cref="LlamaChatTemplate.Open"/>. Zero arguments, returns
/// <see cref="DataKind.String"/>. Pure.
/// </summary>
internal sealed class ChatTemplateOpenFunction : IScalarFunction
{
    private readonly string _name;
    private readonly LlamaChatTemplate _template;

    public ChatTemplateOpenFunction(string name, LlamaChatTemplate template)
    {
        _name = name;
        _template = template;
    }

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new FunctionArgumentException(_name,
                $"expects 0 arguments, got {argumentKinds.Length}.");
        }
        return DataKind.String;
    }

    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ValueRef.FromString(_template.Open));

    public bool IsPure => true;
}

/// <summary>
/// Wraps one message via <see cref="LlamaChatTemplate.WrapMessage"/>.
/// (String, String) → String. Pure.
/// </summary>
/// <remarks>
/// A null role is treated as a hard error — the family-specific
/// validation in <see cref="LlamaChatTemplate.WrapMessage"/> would fail
/// downstream and surfacing the issue here gives a clearer message. A
/// null content is coerced to the empty string so SQL <c>NULL</c> in
/// <c>messages.content</c> doesn't blow up the templating step.
/// </remarks>
internal sealed class ChatTemplateMessageFunction : IScalarFunction
{
    private readonly string _name;
    private readonly LlamaChatTemplate _template;

    public ChatTemplateMessageFunction(string name, LlamaChatTemplate template)
    {
        _name = name;
        _template = template;
    }

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new FunctionArgumentException(_name,
                $"expects 2 arguments (role, content), got {argumentKinds.Length}.");
        }
        FunctionArgumentException.ThrowIfNotStringArgument(_name, 0, "role", argumentKinds[0]);
        FunctionArgumentException.ThrowIfNotStringArgument(_name, 1, "content", argumentKinds[1]);
        return DataKind.String;
    }

    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            throw new FunctionArgumentException(_name,
                "role must not be NULL. Supply 'user', 'assistant', 'system', or 'tool'.");
        }
        string role = args[0].AsString();
        string content = args[1].IsNull ? string.Empty : args[1].AsString();
        return new(ValueRef.FromString(_template.WrapMessage(role, content)));
    }

    public bool IsPure => true;
}

/// <summary>
/// Returns <see cref="LlamaChatTemplate.AssistantTurn"/>. Zero arguments,
/// returns <see cref="DataKind.String"/>. Pure.
/// </summary>
internal sealed class ChatTemplateAssistantTurnFunction : IScalarFunction
{
    private readonly string _name;
    private readonly LlamaChatTemplate _template;

    public ChatTemplateAssistantTurnFunction(string name, LlamaChatTemplate template)
    {
        _name = name;
        _template = template;
    }

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new FunctionArgumentException(_name,
                $"expects 0 arguments, got {argumentKinds.Length}.");
        }
        return DataKind.String;
    }

    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ValueRef.FromString(_template.AssistantTurn));

    public bool IsPure => true;
}
