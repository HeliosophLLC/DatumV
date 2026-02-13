using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

namespace DatumIngest.Tests.LanguageServer;

/// <summary>
/// Tests for <see cref="SignatureHelpProvider"/>: walks back from the cursor
/// to find the enclosing function call, identifies the function (built-in /
/// UDF / model), and reports which parameter the cursor is filling in.
/// </summary>
public sealed class SignatureHelpProviderTests
{
    private static LanguageServerManifest BuildManifest()
    {
        return new LanguageServerManifest
        {
            Tables = [],
            Keywords = [],
            Functions =
            [
                new FunctionSignature
                {
                    Name = "concat",
                    Parameters =
                    [
                        new ParameterSignature { Name = "first", Kind = "STRING" },
                        new ParameterSignature { Name = "second", Kind = "STRING" },
                        new ParameterSignature { Name = "third", Kind = "STRING", IsOptional = true },
                    ],
                    ReturnType = "STRING",
                    Description = "Concatenates string arguments.",
                },
                new FunctionSignature
                {
                    Name = "upper",
                    Parameters = [new ParameterSignature { Name = "value", Kind = "STRING" }],
                    ReturnType = "STRING",
                },
            ],
            Models =
            [
                new ModelEntry
                {
                    Name = "llama31_8b",
                    OutputKind = "STRING",
                    Category = "llm",
                    Backend = "llama",
                    DisplayName = "Llama 3.1 8B Instruct",
                    Parameters =
                    [
                        new ParameterSignature { Name = "input", Kind = "STRING" },
                        new ParameterSignature { Name = "temperature", Kind = "FLOAT64", IsOptional = true },
                    ],
                },
            ],
            Udfs =
            [
                new UdfEntry
                {
                    SchemaName = "public",
                    Name = "RewriteCaption",
                    ReturnType = "STRING",
                    BodyKind = "procedural",
                    IsPure = true,
                    Parameters =
                    [
                        new ParameterSignature { Name = "caption", Kind = "STRING" },
                        new ParameterSignature { Name = "tone", Kind = "STRING", IsOptional = true },
                    ],
                },
            ],
        };
    }

    private static SignatureHelpProvider NewProvider() => new(BuildManifest());

    // ───────────────────── Built-in scalar functions ─────────────────────

    [Fact]
    public void Builtin_AfterOpenParen_ReturnsFirstParameter()
    {
        // Cursor immediately after `concat(` — first parameter active.
        SignatureHelp? sig = NewProvider().GetSignatureHelp("SELECT concat(", 14);

        Assert.NotNull(sig);
        Assert.Single(sig!.Signatures);
        Assert.Equal(0, sig.ActiveParameter);
        Assert.Contains("first: STRING", sig.Signatures[0].Label);
        Assert.Contains("→ STRING", sig.Signatures[0].Label);
    }

    [Fact]
    public void Builtin_AfterFirstComma_AdvancesToSecondParameter()
    {
        // `concat('a', |` — cursor on the second parameter.
        SignatureHelp? sig = NewProvider().GetSignatureHelp("SELECT concat('a', ", 19);

        Assert.NotNull(sig);
        Assert.Equal(1, sig!.ActiveParameter);
    }

    [Fact]
    public void Builtin_PastDeclaredArity_ClampsToLastParameter()
    {
        // Three commas → fourth slot, but the function only has three. Clamp
        // to index 2 (the last) so the popup keeps something visible rather
        // than vanishing as soon as the user types a stray extra comma.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT concat('a', 'b', 'c', ", 29);

        Assert.NotNull(sig);
        Assert.Equal(2, sig!.ActiveParameter);
    }

    [Fact]
    public void Builtin_OutsideAnyCall_ReturnsNull()
    {
        // Plain text with no open paren behind the cursor.
        SignatureHelp? sig = NewProvider().GetSignatureHelp("SELECT 42 + ", 12);

        Assert.Null(sig);
    }

    // ───────────────────── UDF dispatch ─────────────────────

    [Fact]
    public void Udf_AfterOpenParen_PicksFromSchema()
    {
        // Post-S7d UDFs live in real schemas (typically `public`); the
        // signature popup uses the qualified name as the label.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT public.RewriteCaption(", 29);

        Assert.NotNull(sig);
        Assert.Contains("public.RewriteCaption", sig!.Signatures[0].Label);
        Assert.Contains("caption: STRING", sig.Signatures[0].Label);
        Assert.Contains("tone: STRING?", sig.Signatures[0].Label);
        Assert.Equal(0, sig.ActiveParameter);
    }

    [Fact]
    public void Udf_Unqualified_ResolvesThroughSearchPath()
    {
        // Bare names walk search_path — `RewriteCaption` resolves to
        // (public, RewriteCaption) because `public` is on the default
        // path.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT RewriteCaption(", 22);

        Assert.NotNull(sig);
        Assert.Contains("public.RewriteCaption", sig!.Signatures[0].Label);
    }

    [Fact]
    public void Udf_DocumentationCarriesBodyKindAndPurity()
    {
        // Procedural + pure UDF should surface both flags so the popup tells
        // the user what call shape they're invoking.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT public.RewriteCaption(", 29);

        Assert.NotNull(sig);
        string? doc = sig!.Signatures[0].Documentation;
        Assert.NotNull(doc);
        Assert.Contains("procedural", doc);
        Assert.Contains("pure", doc);
    }

    // ───────────────────── Models dispatch ─────────────────────

    [Fact]
    public void Model_AfterOpenParen_PicksFromModelsNamespace()
    {
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT models.llama31_8b(", 25);

        Assert.NotNull(sig);
        Assert.Contains("models.llama31_8b", sig!.Signatures[0].Label);
        Assert.Contains("input: STRING", sig.Signatures[0].Label);
        Assert.Contains("temperature: FLOAT64?", sig.Signatures[0].Label);
    }

    [Fact]
    public void Model_DocumentationIncludesBackendAndDisplayName()
    {
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT models.llama31_8b(", 25);

        Assert.NotNull(sig);
        string? doc = sig!.Signatures[0].Documentation;
        Assert.NotNull(doc);
        Assert.Contains("Llama 3.1 8B Instruct", doc);
        Assert.Contains("llama", doc);
    }

    // ───────────────────── Nesting ─────────────────────

    [Fact]
    public void NestedCall_HighlightsInnerCallNotOuter()
    {
        // `concat(upper(|), 'b')` — cursor inside `upper(...)`, not inside
        // `concat(...)`. The walker has to balance the inner paren and
        // attribute the cursor to `upper`.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT concat(upper(", 20);

        Assert.NotNull(sig);
        Assert.Contains("upper", sig!.Signatures[0].Label);
        Assert.DoesNotContain("concat", sig.Signatures[0].Label);
    }

    [Fact]
    public void NestedCall_AfterInnerCloses_ResolvesToOuterCall()
    {
        // `concat(upper('a'), |` — inner call closed; the outer's second
        // parameter is now active.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT concat(upper('a'), ", 26);

        Assert.NotNull(sig);
        Assert.Contains("concat", sig!.Signatures[0].Label);
        Assert.Equal(1, sig.ActiveParameter);
    }

    [Fact]
    public void UnknownFunction_ReturnsNull()
    {
        // Function name not registered anywhere — popup hides.
        SignatureHelp? sig = NewProvider().GetSignatureHelp(
            "SELECT does_not_exist(", 22);

        Assert.Null(sig);
    }
}
