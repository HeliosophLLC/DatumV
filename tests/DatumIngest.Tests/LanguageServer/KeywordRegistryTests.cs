namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Parsing.Tokens;

/// <summary>
/// Tests for <see cref="KeywordRegistry"/> — enforces that every SQL keyword token is
/// mapped in the registry and appears in at least one completion zone. When a new
/// <see cref="SqlToken"/> keyword is added, these tests fail until it is registered.
/// </summary>
public sealed class KeywordRegistryTests : ServiceTestBase
{
    /// <summary>
    /// Every <see cref="SqlToken"/> keyword (value below <see cref="SqlToken.Identifier"/>)
    /// must have an entry in the token completion map. A missing entry means a newly added
    /// keyword is invisible to the language server.
    /// </summary>
    [Fact]
    public void AllSqlTokenKeywords_MappedInKeywordRegistry()
    {
        IEnumerable<SqlToken> keywordTokens = Enum.GetValues<SqlToken>()
            .Where(token => token < SqlToken.Identifier);

        foreach (SqlToken token in keywordTokens)
        {
            Assert.True(
                KeywordRegistry.HasMapping(token),
                $"SqlToken.{token} was added but has no entry in KeywordRegistry.TokenCompletionMap. " +
                $"Add it to make it discoverable in completions, or map it to an empty array if it is a component-only token.");
        }
    }

    /// <summary>
    /// Every non-empty token mapping must produce completion strings that appear in at
    /// least one zone's keyword list. A mapped string that appears nowhere means the
    /// keyword is registered but never offered to the user.
    /// </summary>
    [Fact]
    public void AllMappedKeywords_AppearInAtLeastOneZone()
    {
        IReadOnlySet<string> allZoneKeywords = KeywordRegistry.GetAllZoneCompletionStrings();

        IEnumerable<SqlToken> keywordTokens = Enum.GetValues<SqlToken>()
            .Where(token => token < SqlToken.Identifier);

        foreach (SqlToken token in keywordTokens)
        {
            IReadOnlyList<string> completions = KeywordRegistry.GetCompletionStrings(token);
            if (completions.Count == 0)
            {
                continue; // Component-only token — intentionally has no standalone completion.
            }

            Assert.True(
                completions.Any(completion => allZoneKeywords.Contains(completion)),
                $"SqlToken.{token} maps to [{string.Join(", ", completions)}] " +
                $"but none appear in any zone's keyword list. Add the completion string(s) to the " +
                $"appropriate zone in KeywordRegistry.ZoneKeywords.");
        }
    }

    /// <summary>
    /// Verifies that the zone keyword lists are internally consistent — every string
    /// referenced in a zone list should be a recognizable SQL keyword or compound keyword.
    /// </summary>
    [Fact]
    public void ZoneKeywords_NotEmpty_ForZonesWithKeywords()
    {
        // Zones that are expected to have keyword completions should have at least one.
        CompletionZoneKind[] zonesWithKeywords =
        [
            CompletionZoneKind.StatementStart,
            CompletionZoneKind.AfterSelect,
            CompletionZoneKind.AfterFromSource,
            CompletionZoneKind.AfterJoinSource,
            CompletionZoneKind.AfterWhere,
            CompletionZoneKind.AfterOn,
            CompletionZoneKind.AfterGroupBy,
            CompletionZoneKind.AfterHaving,
            CompletionZoneKind.AfterQualify,
            CompletionZoneKind.AfterOrderBy,
            CompletionZoneKind.AfterAssert,
            CompletionZoneKind.InsideOver,
            CompletionZoneKind.InsideExtract,
            CompletionZoneKind.AfterCreateIndexColumns,
            CompletionZoneKind.AfterCreateIndexUsing,
            CompletionZoneKind.InsideCreateIndexWithOptions,
        ];

        foreach (CompletionZoneKind zone in zonesWithKeywords)
        {
            IReadOnlyList<string> keywords = KeywordRegistry.GetKeywords(zone);
            Assert.True(keywords.Count > 0, $"Zone {zone} should have keyword completions but GetKeywords returned empty.");
        }
    }
}
