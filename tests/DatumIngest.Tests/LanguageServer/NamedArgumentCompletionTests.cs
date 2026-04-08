namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="CompletionProvider"/>'s PG-style named-argument
/// completion path: when the cursor sits at the start of a function
/// call's argument slot, the provider offers each unused parameter as
/// a <c>name := </c> completion item with
/// <see cref="CompletionItemKind.Property"/>.
/// </summary>
public sealed class NamedArgumentCompletionTests : ServiceTestBase
{
    private static LanguageServerManifest CreateManifest() => new()
    {
        Tables = [],
        Functions =
        [
            new FunctionSignature
            {
                SchemaName = "system",
                Name = "blend",
                Parameters =
                [
                    new ParameterSignature { Name = "background", Kind = "Image" },
                    new ParameterSignature { Name = "foreground", Kind = "Image" },
                    new ParameterSignature { Name = "mode", Kind = "String", IsOptional = true },
                    new ParameterSignature { Name = "amount", Kind = "Float32", IsOptional = true },
                ],
                ReturnType = "Image",
                Description = "Blends two images.",
            },
        ],
        Udfs =
        [
            new UdfEntry
            {
                SchemaName = "public",
                Name = "subtract",
                ReturnType = "Int32",
                BodyKind = "procedural",
                Parameters =
                [
                    new ParameterSignature { Name = "a", Kind = "Int32" },
                    new ParameterSignature { Name = "b", Kind = "Int32" },
                ],
            },
        ],
        Keywords = ["SELECT", "FROM"],
    };

    private static CompletionProvider CreateProvider() => new(CreateManifest());

    [Fact]
    public void GetCompletions_AtStartOfFirstArg_OffersEveryParameter()
    {
        CompletionProvider provider = CreateProvider();
        const string sql = "SELECT blend(";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.Contains(items, i => i.Label == "background := " && i.Kind == CompletionItemKind.Property);
        Assert.Contains(items, i => i.Label == "foreground := " && i.Kind == CompletionItemKind.Property);
        Assert.Contains(items, i => i.Label == "mode := " && i.Kind == CompletionItemKind.Property);
        Assert.Contains(items, i => i.Label == "amount := " && i.Kind == CompletionItemKind.Property);
    }

    [Fact]
    public void GetCompletions_AfterFirstPositional_SkipsConsumedParameter()
    {
        CompletionProvider provider = CreateProvider();
        const string sql = "SELECT blend(bg, ";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        // First positional arg consumed `background`; it should no
        // longer surface as a name suggestion.
        Assert.DoesNotContain(items, i => i.Label == "background := ");
        Assert.Contains(items, i => i.Label == "foreground := ");
        Assert.Contains(items, i => i.Label == "mode := ");
        Assert.Contains(items, i => i.Label == "amount := ");
    }

    [Fact]
    public void GetCompletions_AfterNamedArgument_SkipsThatName()
    {
        CompletionProvider provider = CreateProvider();
        const string sql = "SELECT blend(background := bg, ";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.DoesNotContain(items, i => i.Label == "background := ");
        Assert.Contains(items, i => i.Label == "foreground := ");
        Assert.Contains(items, i => i.Label == "mode := ");
    }

    [Fact]
    public void GetCompletions_OptionalParametersTagged()
    {
        CompletionProvider provider = CreateProvider();
        const string sql = "SELECT blend(";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        CompletionItem mode = Assert.Single(items, i => i.Label == "mode := ");
        Assert.Contains("optional", mode.Detail, StringComparison.OrdinalIgnoreCase);

        CompletionItem background = Assert.Single(items, i => i.Label == "background := ");
        Assert.DoesNotContain("optional", background.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCompletions_UdfCall_OffersParameterNames()
    {
        CompletionProvider provider = CreateProvider();
        const string sql = "SELECT subtract(";
        CompletionItem[] items = provider.GetCompletions(sql, sql.Length);

        Assert.Contains(items, i => i.Label == "a := " && i.Kind == CompletionItemKind.Property);
        Assert.Contains(items, i => i.Label == "b := " && i.Kind == CompletionItemKind.Property);
    }

    [Fact]
    public void GetCompletions_OutsideAnyCall_OffersNoNamedArguments()
    {
        CompletionProvider provider = CreateProvider();
        // Cursor sits in the SELECT projection list, not inside any call's parens.
        const string sql = "SELECT  FROM t";
        CompletionItem[] items = provider.GetCompletions(sql, 7);

        Assert.DoesNotContain(items, i => i.Kind == CompletionItemKind.Property);
    }
}
