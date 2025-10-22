namespace DatumIngest.Tests.Editor;

using DatumIngest.Editor;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
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
        HashSet<string> completionTypes = new(KeywordRegistry.ColumnTypeKeywords, StringComparer.OrdinalIgnoreCase);

        foreach (string kindName in Enum.GetNames<DataKind>())
        {
            Assert.True(
                completionTypes.Contains(kindName),
                $"DataKind.{kindName} is missing from KeywordRegistry.ColumnTypeKeywords. " +
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

    // ───────────────────── Date part keyword sync ─────────────────────

    /// <summary>
    /// Every name in <see cref="MonarchGrammarFactory.DatePartKeywords"/> and
    /// <see cref="KeywordRegistry.DatePartFieldNames"/> must be accepted by
    /// <see cref="DatePartFunction"/> without throwing. This detects drift when
    /// a field name is added to highlighting/completions but not to the runtime,
    /// or vice versa.
    /// </summary>
    [Fact]
    public void DatePartKeywords_AllAcceptedByDatePartFunction()
    {
        DatePartFunction function = new();

        // Use a DateTime with non-zero components so all fields produce meaningful results.
        DataValue dateTime = DataValue.FromDateTime(
            new DateTimeOffset(2026, 6, 15, 14, 30, 45, 500, TimeSpan.FromHours(5)));

        HashSet<string> allNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in MonarchGrammarFactory.DatePartKeywords())
            allNames.Add(name);
        foreach (string name in KeywordRegistry.DatePartFieldNames)
            allNames.Add(name);

        List<string> rejected = [];
        foreach (string name in allNames)
        {
            try
            {
                function.Execute([DataValue.FromString(name), dateTime]);
            }
            catch (ArgumentException)
            {
                rejected.Add(name);
            }
        }

        Assert.True(
            rejected.Count == 0,
            $"The following date part names are in DatePartKeywords/DatePartFieldNames but rejected by DatePartFunction: " +
            $"{string.Join(", ", rejected)}. Add them to DatePartFunction or remove them from the keyword lists.");
    }

    /// <summary>
    /// Ensures <see cref="KeywordRegistry.DatePartFieldNames"/> is a superset of
    /// <see cref="MonarchGrammarFactory.DatePartKeywords"/> — every highlighted name
    /// should also be offered in autocomplete.
    /// </summary>
    [Fact]
    public void DatePartKeywords_AllInCompletionProvider()
    {
        HashSet<string> completions = new(KeywordRegistry.DatePartFieldNames, StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach (string name in MonarchGrammarFactory.DatePartKeywords())
        {
            if (!completions.Contains(name))
            {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"The following Monarch DatePartKeywords are missing from KeywordRegistry.DatePartFieldNames: " +
            $"{string.Join(", ", missing)}. Add them so they appear in EXTRACT autocomplete.");
    }
}
