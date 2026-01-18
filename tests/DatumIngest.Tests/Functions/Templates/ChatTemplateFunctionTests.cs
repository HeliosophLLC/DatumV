using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Templates;

/// <summary>
/// Tests for the <c>templates.{family}_*</c> scalar functions registered
/// by <see cref="DatumIngest.Functions.Templates.ChatTemplateFunctions"/>.
/// </summary>
/// <remarks>
/// Each family registers three functions: <c>_open()</c>, <c>_msg(role,
/// content)</c>, <c>_assistant_turn()</c>. These tests resolve them by
/// name from a default <see cref="FunctionRegistry"/> and exercise the
/// composition that SQL would produce, locking in the expected wire-text
/// output for at least one multi-turn conversation per family.
/// </remarks>
public sealed class ChatTemplateFunctionTests
{
    /// <summary>
    /// Composes the equivalent of:
    ///   templates.{fam}_open()
    ///   || templates.{fam}_msg(role, content)  (× n)
    ///   || templates.{fam}_assistant_turn()
    /// by resolving the three functions from the registry and invoking
    /// them in order. Lets the assertions match exactly what a SQL
    /// pipeline would produce, not just the underlying LlamaChatTemplate.
    /// </summary>
    private static string ComposeViaFunctions(
        FunctionRegistry registry,
        string family,
        params (string Role, string Content)[] msgs)
    {
        IScalarFunction open = ResolveOrThrow(registry, $"templates.{family}_open");
        IScalarFunction msg = ResolveOrThrow(registry, $"templates.{family}_msg");
        IScalarFunction asst = ResolveOrThrow(registry, $"templates.{family}_assistant_turn");

        string result = Invoke(open).AsString();
        foreach ((string role, string content) in msgs)
        {
            result += Invoke(msg, ValueRef.FromString(role), ValueRef.FromString(content)).AsString();
        }
        result += Invoke(asst).AsString();
        return result;
    }

    private static IScalarFunction ResolveOrThrow(FunctionRegistry r, string name)
    {
        IScalarFunction? f = r.TryGetScalar(name);
        Assert.NotNull(f);
        return f;
    }

    private static ValueRef Invoke(IScalarFunction fn, params ValueRef[] arguments)
    {
        EvaluationFrame frame = default;
        return fn.ExecuteAsync(arguments, frame, default).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("llama31")]
    [InlineData("phi3")]
    [InlineData("zephyr")]
    [InlineData("gemma")]
    [InlineData("chatml")]
    [InlineData("mistral")]
    [InlineData("granite")]
    public void EveryFamilyRegistersAllThreeFunctions(string family)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.NotNull(registry.TryGetScalar($"templates.{family}_open"));
        Assert.NotNull(registry.TryGetScalar($"templates.{family}_msg"));
        Assert.NotNull(registry.TryGetScalar($"templates.{family}_assistant_turn"));
    }

    [Theory]
    [InlineData("llama31")]
    [InlineData("phi3")]
    [InlineData("zephyr")]
    [InlineData("gemma")]
    [InlineData("chatml")]
    [InlineData("mistral")]
    [InlineData("granite")]
    public void EveryFamilyHasDescriptorsWithStringCategory(string family)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        foreach (string suffix in new[] { "open", "msg", "assistant_turn" })
        {
            FunctionDescriptor? d = registry.TryGetScalarDescriptor($"templates.{family}_{suffix}");
            Assert.NotNull(d);
            Assert.Equal(FunctionCategory.String, d.Category);
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
        }
    }

    [Fact]
    public void Llama31_ComposeViaFunctions_MatchesExpectedWireText()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        string actual = ComposeViaFunctions(
            registry, "llama31",
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
    public void Phi3_ComposeViaFunctions_MatchesExpectedWireText()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        string actual = ComposeViaFunctions(
            registry, "phi3",
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
    public void ChatML_ComposeViaFunctions_MatchesExpectedWireText()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        string actual = ComposeViaFunctions(
            registry, "chatml",
            ("system", "You are helpful."),
            ("user", "Hi."),
            ("assistant", "Hello!"));

        const string expected =
            "<|im_start|>system\nYou are helpful.<|im_end|>\n" +
            "<|im_start|>user\nHi.<|im_end|>\n" +
            "<|im_start|>assistant\nHello!<|im_end|>\n" +
            "<|im_start|>assistant\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Gemma_ComposeViaFunctions_MapsAssistantToModel()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        string actual = ComposeViaFunctions(
            registry, "gemma",
            ("user", "Hi."),
            ("assistant", "Hello!"));

        const string expected =
            "<start_of_turn>user\nHi.<end_of_turn>\n" +
            "<start_of_turn>model\nHello!<end_of_turn>\n" +
            "<start_of_turn>model\n";

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Mistral_System_ThrowsClearError()
    {
        // Mistral has no native system role — surfacing the family
        // limitation through the templates.mistral_msg function.
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction msg = ResolveOrThrow(registry, "templates.mistral_msg");
        Exception ex = Assert.ThrowsAny<Exception>(() =>
            Invoke(msg, ValueRef.FromString("system"), ValueRef.FromString("You are helpful.")));
        Assert.Contains("system", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Msg_NullRole_RaisesFunctionArgumentException()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction msg = ResolveOrThrow(registry, "templates.llama31_msg");
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            Invoke(msg, ValueRef.Null(DataKind.String), ValueRef.FromString("Hi.")));
        Assert.Contains("role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Msg_NullContent_TreatedAsEmptyString()
    {
        // SQL NULL in messages.content shouldn't blow up the templating
        // step — empty content is well-defined inside any family's wrap.
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction msg = ResolveOrThrow(registry, "templates.llama31_msg");
        ValueRef result = Invoke(msg, ValueRef.FromString("user"), ValueRef.Null(DataKind.String));
        Assert.False(result.IsNull);
        Assert.Equal(
            "<|start_header_id|>user<|end_header_id|>\n\n<|eot_id|>",
            result.AsString());
    }

    [Fact]
    public void Open_RejectsAnyArgument()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction open = ResolveOrThrow(registry, "templates.llama31_open");
        Assert.Throws<FunctionArgumentException>(() =>
            open.ValidateArguments([DataKind.String]));
    }

    [Fact]
    public void Msg_RejectsWrongArity()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction msg = ResolveOrThrow(registry, "templates.llama31_msg");
        Assert.Throws<FunctionArgumentException>(() => msg.ValidateArguments([DataKind.String]));
        Assert.Throws<FunctionArgumentException>(() =>
            msg.ValidateArguments([DataKind.String, DataKind.String, DataKind.String]));
    }

    [Fact]
    public void Msg_RejectsNonStringArguments()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction msg = ResolveOrThrow(registry, "templates.llama31_msg");
        Assert.Throws<FunctionArgumentException>(() =>
            msg.ValidateArguments([DataKind.Int32, DataKind.String]));
    }

    [Fact]
    public void TemplateFunctions_AreMarkedPure()
    {
        // CSE eligibility — same family/role/content always produces the
        // same string. The model invocation that wraps these isn't pure,
        // but the template-text helpers themselves are.
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.True(ResolveOrThrow(registry, "templates.llama31_open").IsPure);
        Assert.True(ResolveOrThrow(registry, "templates.llama31_msg").IsPure);
        Assert.True(ResolveOrThrow(registry, "templates.llama31_assistant_turn").IsPure);
    }
}
