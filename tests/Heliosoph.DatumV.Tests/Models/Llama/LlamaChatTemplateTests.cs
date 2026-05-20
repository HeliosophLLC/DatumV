using Heliosoph.DatumV.Models.Llama;

namespace Heliosoph.DatumV.Tests.Models.Llama;

/// <summary>
/// Pure-string round-trip tests for the per-family <see cref="LlamaChatTemplate"/>
/// primitives. No GGUF model required — these exercise the templating logic
/// in isolation so CI can run them on any machine.
/// </summary>
/// <remarks>
/// Each family has one <c>FormatsThreeTurnConversation</c> test that emits a
/// fixed reference string for the same {system, user, assistant, user}
/// conversation fragment. The reference strings are hand-verified against
/// each family's published template once and then locked — any change to the
/// template's WrapMessage / Open / AssistantTurn primitives will surface as a
/// diff in the assertion.
/// </remarks>
public sealed class LlamaChatTemplateTests
{
    /// <summary>
    /// Helper: composes a 4-turn conversation fragment using the family's
    /// primitives. Equivalent to what the SQL pattern does:
    ///
    ///   templates.{fam}_open()
    ///   || templates.{fam}_msg('system', s)
    ///   || templates.{fam}_msg('user', u1)
    ///   || templates.{fam}_msg('assistant', a)
    ///   || templates.{fam}_msg('user', u2)
    ///   || templates.{fam}_assistant_turn()
    /// </summary>
    private static string Compose(LlamaChatTemplate t, params (string Role, string Content)[] msgs)
    {
        string s = t.Open;
        foreach ((string role, string content) in msgs)
        {
            s += t.WrapMessage(role, content);
        }
        return s + t.AssistantTurn;
    }

    [Fact]
    public void Llama31_FormatsThreeTurnConversation()
    {
        string actual = Compose(
            LlamaChatTemplate.Llama31,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<|start_header_id|>system<|end_header_id|>\n\nYou are helpful.<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n\nHi.<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\nHello!<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n\nBye.<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Llama31_ToolRoleMapsToIPython()
    {
        // Llama 3.1's tool-call return role is "ipython"; SQL author passes
        // 'tool' and the template handles the rename internally.
        string actual = LlamaChatTemplate.Llama31.WrapMessage("tool", "{\"result\": 42}");
        Assert.Equal(
            "<|start_header_id|>ipython<|end_header_id|>\n\n{\"result\": 42}<|eot_id|>",
            actual);
    }

    [Fact]
    public void Phi3_FormatsThreeTurnConversation()
    {
        string actual = Compose(
            LlamaChatTemplate.Phi3,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<|system|>\nYou are helpful.<|end|>\n" +
            "<|user|>\nHi.<|end|>\n" +
            "<|assistant|>\nHello!<|end|>\n" +
            "<|user|>\nBye.<|end|>\n" +
            "<|assistant|>\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Zephyr_FormatsThreeTurnConversation()
    {
        string actual = Compose(
            LlamaChatTemplate.Zephyr,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<|system|>\nYou are helpful.</s>\n" +
            "<|user|>\nHi.</s>\n" +
            "<|assistant|>\nHello!</s>\n" +
            "<|user|>\nBye.</s>\n" +
            "<|assistant|>\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Gemma_AssistantRoleEmitsModelKeyword()
    {
        // Gemma is the family quirk: 'assistant' on the SQL side maps to
        // the literal 'model' role keyword inside the template. Gemma also
        // has no native system role; system messages fold into user turns.
        string actual = Compose(
            LlamaChatTemplate.Gemma,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<start_of_turn>user\nYou are helpful.<end_of_turn>\n" +
            "<start_of_turn>user\nHi.<end_of_turn>\n" +
            "<start_of_turn>model\nHello!<end_of_turn>\n" +
            "<start_of_turn>user\nBye.<end_of_turn>\n" +
            "<start_of_turn>model\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ChatML_FormatsThreeTurnConversation()
    {
        string actual = Compose(
            LlamaChatTemplate.ChatML,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<|im_start|>system\nYou are helpful.<|im_end|>\n" +
            "<|im_start|>user\nHi.<|im_end|>\n" +
            "<|im_start|>assistant\nHello!<|im_end|>\n" +
            "<|im_start|>user\nBye.<|im_end|>\n" +
            "<|im_start|>assistant\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Mistral_FormatsTwoTurnConversation()
    {
        // Mistral's [INST]/[/INST] structure plus </s> after assistant turns.
        // No system role — see SystemRoleThrows.
        string actual = Compose(
            LlamaChatTemplate.Mistral,
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "[INST] Hi. [/INST]" +
            "Hello!</s> " +
            "[INST] Bye. [/INST]" +
            " [/INST]";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Mistral_SystemRoleThrows()
    {
        // Mistral has no native system role. Template surfaces this as an
        // explicit error so the caller folds the system text into the first
        // user message manually.
        Assert.Throws<ArgumentException>(() =>
            LlamaChatTemplate.Mistral.WrapMessage("system", "You are helpful."));
    }

    [Fact]
    public void Granite_FormatsThreeTurnConversation()
    {
        string actual = Compose(
            LlamaChatTemplate.Granite,
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"),
            ("user", "Bye."));

        const string expected =
            "<|start_of_role|>system<|end_of_role|>You are helpful.<|end_of_text|>\n" +
            "<|start_of_role|>user<|end_of_role|>Hi.<|end_of_text|>\n" +
            "<|start_of_role|>assistant<|end_of_role|>Hello!<|end_of_text|>\n" +
            "<|start_of_role|>user<|end_of_role|>Bye.<|end_of_text|>\n" +
            "<|start_of_role|>assistant<|end_of_role|>";

        Assert.Equal(expected, actual);
    }

    // Identity_PassesContentThrough test retired alongside the
    // LlamaChatTemplate.Identity template. The no-wrap identity template
    // existed only to support the `templated` opt-arg on LlamaModel,
    // which paired with the templates.X scalars for SQL-side prompt
    // assembly. With the SQL-LLM migration through llama_chat()
    // (llama.cpp's native chat-template engine drives prompt formatting)
    // there is no longer a "pre-templated" path to bypass.

    [Fact]
    public void LegacyFormat_StillWrapsSingleUserMessage()
    {
        // The single-arg Format helper preserves the pre-refactor shape so
        // existing one-shot callsites (LlamaModel.InferStreamingAsync) keep
        // working with the new data model. Equivalent to:
        //   Open + WrapMessage('user', msg) + AssistantTurn
        string actual = LlamaChatTemplate.Llama31.Format("Test message");
        const string expected =
            "<|start_header_id|>user<|end_header_id|>\n\nTest message<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\n";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StripTrailingStop_RemovesEmittedStopMarker()
    {
        // Defensive scrub for cases where the executor's anti-prompt list
        // doesn't catch the stop mid-stream and the literal text leaks into
        // the output.
        string actual = LlamaChatTemplate.Llama31.StripTrailingStop("Hello there<|eot_id|>");
        Assert.Equal("Hello there", actual);
    }

    [Theory]
    [InlineData("user", "Hi.")]
    [InlineData("assistant", "Hi.")]
    public void Llama31_RoundTripPreservesContent(string role, string content)
    {
        // Sanity property: the wrapped chunk contains the original content
        // verbatim. Catches regressions where a refactor accidentally
        // double-encodes or mangles bracket characters.
        string wrapped = LlamaChatTemplate.Llama31.WrapMessage(role, content);
        Assert.Contains(content, wrapped);
    }
}
