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
