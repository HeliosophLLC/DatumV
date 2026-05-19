using System.Text;

using Heliosoph.DatumV.Parsing.Tokens;

using Superpower.Model;

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// Source-level rewrite that takes a catalog installSql blob and
/// substitutes each declared bare model identifier with its
/// <see cref="CatalogVersionModel.EffectivePinnedAs"/> form. Used by
/// <c>CatalogBackedModelInstaller</c> at install time and by
/// <c>TableCatalog.RehydrateModelsAsync</c> when reviving a pinned-mode
/// row from a previous process.
/// </summary>
/// <remarks>
/// <para>
/// Walks tokens until a <c>CREATE [OR REPLACE] MODEL &lt;Identifier&gt;</c>
/// sequence appears, then substitutes only the identifier's source range.
/// Comments and every other identifier mention pass through untouched.
/// USING-path resolution to the pinned version's folder is handled
/// separately by <see cref="ModelInstallContext.CurrentVersionPin"/>; this
/// rewriter never touches USING strings.
/// </para>
/// </remarks>
public static class PinnedInstallSqlRewriter
{
    /// <summary>
    /// Returns <paramref name="sql"/> with every declared bare model
    /// identifier replaced by its pinned-form counterpart. Returns the
    /// input unchanged when the version declares no identifiers, when no
    /// declared identifier diverges from its pinned form, or when the
    /// tokeniser rejects the input.
    /// </summary>
    public static string Rewrite(string sql, CatalogVersion version)
    {
        if (version.Models is null || version.Models.Count == 0) { return sql; }

        Dictionary<string, string> pinMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogVersionModel vm in version.Models)
        {
            string pinnedAs = vm.EffectivePinnedAs(version.Version);
            if (!string.Equals(pinnedAs, vm.Identifier, StringComparison.Ordinal))
            {
                pinMap[vm.Identifier] = pinnedAs;
            }
        }
        if (pinMap.Count == 0) { return sql; }

        TokenList<SqlToken> tokens;
        try
        {
            tokens = SqlTokenizer.Instance.Tokenize(sql);
        }
        catch (Superpower.ParseException)
        {
            // Tokeniser refused: let the downstream batch parser surface the
            // diagnostic against the original source; rewriting against a
            // malformed baseline would just confuse the error.
            return sql;
        }

        List<(int Start, int Length, string Replacement)> subs = [];
        Token<SqlToken>[] arr = tokens.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].Kind != SqlToken.Create) { continue; }
            int j = i + 1;
            if (j + 1 < arr.Length
                && arr[j].Kind == SqlToken.Or
                && arr[j + 1].Kind == SqlToken.Replace)
            {
                j += 2;
            }
            // Soft `MODEL` keyword tokenises as an Identifier with text "MODEL".
            if (j >= arr.Length
                || arr[j].Kind != SqlToken.Identifier
                || !arr[j].ToStringValue().Equals("MODEL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int identIndex = j + 1;
            if (identIndex >= arr.Length || arr[identIndex].Kind != SqlToken.Identifier)
            {
                continue;
            }
            Token<SqlToken> identTok = arr[identIndex];
            if (!pinMap.TryGetValue(identTok.ToStringValue(), out string? pinnedAs))
            {
                continue;
            }
            subs.Add((
                Start: identTok.Span.Position.Absolute,
                Length: identTok.Span.Length,
                Replacement: pinnedAs));
        }

        if (subs.Count == 0) { return sql; }

        subs.Sort(static (a, b) => b.Start.CompareTo(a.Start));
        StringBuilder builder = new(sql);
        foreach ((int start, int length, string replacement) in subs)
        {
            builder.Remove(start, length);
            builder.Insert(start, replacement);
        }
        return builder.ToString();
    }
}
