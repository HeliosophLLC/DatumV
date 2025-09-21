namespace DatumIngest.Tests.Editor;

using DatumIngest.Editor;
using DatumIngest.Functions;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;
using DatumIngest.Model;

/// <summary>
/// Ensures the hard-coded lists in <see cref="MonarchGrammarFactory"/>,
/// <see cref="CompletionProvider"/>, and <see cref="HoverProvider"/>
/// stay in sync with the authoritative sources (<see cref="DataKind"/>
/// enum and <see cref="FunctionDocumentation"/>).
/// If a function or type is added without updating the grammar, these
/// tests will fail.
/// </summary>
public sealed class MonarchGrammarSyncTests
{
    // ───────────────────── Type name sync ─────────────────────

    [Fact]
    public void TypeKeywords_CoversAllDataKindNames()
    {
        HashSet<string> monarchTypes = new(MonarchGrammarFactory.TypeKeywords(), StringComparer.OrdinalIgnoreCase);

        foreach (string kindName in Enum.GetNames<DataKind>())
        {
            Assert.True(
                monarchTypes.Contains(kindName),
                $"DataKind.{kindName} is missing from MonarchGrammarFactory.TypeKeywords(). " +
                $"Add \"{kindName}\" so it gets syntax highlighting.");
        }
    }

    [Fact]
    public void CompletionProvider_ColumnTypeKeywords_CoversAllDataKindNames()
    {
        HashSet<string> completionTypes = new(CompletionProvider.ColumnTypeKeywords, StringComparer.OrdinalIgnoreCase);

        foreach (string kindName in Enum.GetNames<DataKind>())
        {
            Assert.True(
                completionTypes.Contains(kindName),
                $"DataKind.{kindName} is missing from CompletionProvider.ColumnTypeKeywords. " +
                $"Add \"{kindName}\" so it appears in CREATE TABLE completions.");
        }
    }

    [Fact]
    public void HoverProvider_TypeDescriptions_CoversAllDataKindNames()
    {
        foreach (string kindName in Enum.GetNames<DataKind>())
        {
            Assert.True(
                HoverProvider.TypeDescriptions.ContainsKey(kindName),
                $"DataKind.{kindName} is missing from HoverProvider.TypeDescriptions. " +
                $"Add a hover description for \"{kindName}\".");
        }
    }

    // ───────────────────── Manifest keyword sync ─────────────────────

    /// <summary>
    /// The language-server manifest in Program.cs hard-codes a keyword list
    /// (to avoid a project reference to DatumIngest.Editor). This test ensures
    /// every keyword in that list is present in the authoritative grammar lists
    /// (<see cref="MonarchGrammarFactory.ClauseKeywords"/> + boolNullKeywords).
    /// </summary>
    [Fact]
    public void ManifestKeywords_AreAllInMonarchGrammar()
    {
        HashSet<string> grammar = new(MonarchGrammarFactory.ClauseKeywords(), StringComparer.OrdinalIgnoreCase);
        grammar.Add("TRUE");
        grammar.Add("FALSE");
        grammar.Add("NULL");

        List<string> unexpected = [];
        foreach (string kw in Program.ManifestKeywords())
        {
            if (!grammar.Contains(kw))
            {
                unexpected.Add(kw);
            }
        }

        Assert.True(
            unexpected.Count == 0,
            $"ManifestKeywords() contains entries not in MonarchGrammarFactory: {string.Join(", ", unexpected)}. " +
            $"Add them to ClauseKeywords() or boolNullKeywords, or remove them from ManifestKeywords().");
    }

    /// <summary>
    /// Ensures that the manifest keyword list is not missing any keywords
    /// from the authoritative Monarch grammar. When a new keyword is added
    /// to <see cref="MonarchGrammarFactory.ClauseKeywords"/> it should also
    /// appear in the manifest so the language server advertises it.
    /// </summary>
    [Fact]
    public void ManifestKeywords_CoversAllMonarchKeywords()
    {
        HashSet<string> manifest = new(Program.ManifestKeywords(), StringComparer.OrdinalIgnoreCase);

        // The manifest includes TRUE/FALSE/NULL, which live in boolNullKeywords
        // in the grammar. Combine both authoritative lists for the comparison.
        List<string> allGrammarKeywords = [.. MonarchGrammarFactory.ClauseKeywords(), "TRUE", "FALSE", "NULL"];

        List<string> missing = [];
        foreach (string kw in allGrammarKeywords)
        {
            if (!manifest.Contains(kw))
            {
                missing.Add(kw);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following Monarch grammar keywords are missing from ManifestKeywords(): {string.Join(", ", missing)}. " +
            $"Add them to ManifestKeywords() in Program.cs so the language server manifest stays in sync.");
    }

    // ───────────────────── Function name sync ─────────────────────

    /// <summary>
    /// Function names that are already covered by the <c>@keywords</c> list in the
    /// Monarch grammar. These are SQL keywords that double as function names, so they
    /// are intentionally excluded from <c>@builtinFunctions</c> — the keyword color
    /// takes precedence and they would never match the function case anyway.
    /// </summary>
    private static readonly HashSet<string> KeywordOverlaps =
        new(MonarchGrammarFactory.ClauseKeywords(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BuiltinFunctions_CoversAllDocumentedFunctions()
    {
        HashSet<string> monarchFunctions = new(MonarchGrammarFactory.BuiltinFunctions(), StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach (FunctionSignature function in FunctionDocumentation.All)
        {
            if (KeywordOverlaps.Contains(function.Name))
            {
                continue;
            }

            if (!monarchFunctions.Contains(function.Name))
            {
                missing.Add(function.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following documented functions are missing from MonarchGrammarFactory.BuiltinFunctions(): " +
            $"{string.Join(", ", missing)}. Add them so they get syntax highlighting.");
    }

    [Fact]
    public void BuiltinFunctions_ContainsNoStaleEntries()
    {
        HashSet<string> documentedNames = new(
            FunctionDocumentation.All.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        List<string> stale = [];
        foreach (string name in MonarchGrammarFactory.BuiltinFunctions())
        {
            if (!documentedNames.Contains(name) && !KeywordOverlaps.Contains(name))
            {
                stale.Add(name);
            }
        }

        Assert.True(
            stale.Count == 0,
            $"The following entries in MonarchGrammarFactory.BuiltinFunctions() are not in FunctionDocumentation: " +
            $"{string.Join(", ", stale)}. Remove them or add documentation.");
    }
}
