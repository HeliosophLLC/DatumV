using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

namespace DatumIngest.Tests.LanguageServer;

/// <summary>
/// Phase-A6: <see cref="FunctionContextResolver"/> computes the
/// effective lambda-body whitelist from manifest data, mirroring
/// <c>FunctionRegistry.IsVisibleInContext</c> at edit time.
/// </summary>
public sealed class FunctionContextResolverTests
{
    private static LanguageServerManifest MakeManifest(
        IReadOnlyList<FunctionSignature> functions,
        IReadOnlyList<FunctionContextEntry>? contexts) =>
        new()
        {
            Tables = Array.Empty<TableSchemaEntry>(),
            Functions = functions,
            Keywords = Array.Empty<string>(),
            FunctionContexts = contexts,
        };

    private static FunctionSignature Fn(string name, IReadOnlyList<string>? contexts = null) =>
        new()
        {
            Name = name,
            Parameters = Array.Empty<ParameterSignature>(),
            Contexts = contexts,
        };

    private static FunctionContextEntry Ctx(string name, string? parent = null, params string[] borrows) =>
        new()
        {
            Name = name,
            Parameters = Array.Empty<LambdaParameterEntry>(),
            ParentName = parent,
            Borrows = borrows,
        };

    [Fact]
    public void NoContextsManifest_FallsBackToGlobalsOnly()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("global1"), Fn("global2"), Fn("restricted", contexts: ["animation"])],
            contexts: null);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("animation", manifest);

        // With no context info available, only globally-visible functions
        // (empty Contexts list) make the cut. Restricted functions stay hidden.
        Assert.Contains("global1", whitelist);
        Assert.Contains("global2", whitelist);
        Assert.DoesNotContain("restricted", whitelist);
    }

    [Fact]
    public void GlobalFunctions_VisibleInsideAnyContext()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("plus"), Fn("oscillate", contexts: ["animation"])],
            contexts: [Ctx("pure"), Ctx("animation", parent: "pure")]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("animation", manifest);

        Assert.Contains("plus", whitelist);
        Assert.Contains("oscillate", whitelist);
    }

    [Fact]
    public void ContextRestricted_HiddenInUnrelatedContext()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("oscillate", contexts: ["animation"])],
            contexts: [Ctx("pure"), Ctx("animation", parent: "pure"), Ctx("unrelated", parent: "pure")]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("unrelated", manifest);

        Assert.DoesNotContain("oscillate", whitelist);
    }

    [Fact]
    public void ContextRestricted_VisibleInDescendantContext()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("oscillate", contexts: ["animation"])],
            contexts:
            [
                Ctx("pure"),
                Ctx("animation", parent: "pure"),
                Ctx("extended", parent: "animation"),  // descendant of animation
            ]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("extended", manifest);

        Assert.Contains("oscillate", whitelist);
    }

    [Fact]
    public void Borrows_ExtendWhitelist()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("rotate", contexts: ["image"]), Fn("plus")],
            contexts:
            [
                Ctx("pure"),
                Ctx("image"),
                Ctx("animation", parent: "pure", borrows: ["rotate"]),
            ]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("animation", manifest);

        Assert.Contains("rotate", whitelist);  // borrowed in
        Assert.Contains("plus", whitelist);    // globally visible
    }

    [Fact]
    public void GetCanonicalParameters_ReturnsContextParameters()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: Array.Empty<FunctionSignature>(),
            contexts:
            [
                new FunctionContextEntry
                {
                    Name = "animation",
                    Parameters = [new LambdaParameterEntry { Name = "t", Kind = "Float32" }],
                    Borrows = Array.Empty<string>(),
                },
            ]);

        IReadOnlyList<LambdaParameterEntry>? parameters =
            FunctionContextResolver.GetCanonicalParameters("animation", manifest);

        Assert.NotNull(parameters);
        Assert.Single(parameters);
        Assert.Equal("t", parameters[0].Name);
        Assert.Equal("Float32", parameters[0].Kind);
    }

    [Fact]
    public void GetCanonicalParameters_UnknownContext_ReturnsNull()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: Array.Empty<FunctionSignature>(),
            contexts: [Ctx("animation")]);

        Assert.Null(FunctionContextResolver.GetCanonicalParameters("missing", manifest));
    }

    [Fact]
    public void UnknownContext_FallsBackToGlobalsOnly()
    {
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("plus"), Fn("oscillate", contexts: ["animation"])],
            contexts: [Ctx("pure"), Ctx("animation", parent: "pure")]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("nonexistent", manifest);

        Assert.Contains("plus", whitelist);
        Assert.DoesNotContain("oscillate", whitelist);
    }

    [Fact]
    public void AncestorChainCycle_TerminatesCleanly()
    {
        // Defensive: a malformed manifest with a cycle in ParentName shouldn't hang.
        LanguageServerManifest manifest = MakeManifest(
            functions: [Fn("plus"), Fn("oscillate", contexts: ["animation"])],
            contexts:
            [
                Ctx("animation", parent: "animation"),  // self-cycle
            ]);

        HashSet<string> whitelist = FunctionContextResolver.EffectiveWhitelist("animation", manifest);

        Assert.Contains("oscillate", whitelist);
        Assert.Contains("plus", whitelist);
    }
}
