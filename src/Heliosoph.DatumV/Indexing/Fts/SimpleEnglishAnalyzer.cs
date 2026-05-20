using System.Collections.Frozen;
using System.Text;

namespace Heliosoph.DatumV.Indexing.Fts;

/// <summary>
/// Minimal English-leaning analyzer: Unicode letter/digit runs are the
/// tokens, lowercased via <see cref="Rune.ToLowerInvariant(System.Text.Rune)"/>,
/// with a short stop-word list and a 2-character minimum. No stemming.
/// </summary>
/// <remarks>
/// <para>This is the v1 baseline. It's good enough for chat search where
/// users type the same words they remember seeing; it falls down on
/// inflection ("run" vs "running") and on non-English languages. A Porter
/// stemmer and per-language analyzers are follow-ups.</para>
///
/// <para>The tokenizer treats any maximal run of <see cref="Rune.IsLetterOrDigit"/>
/// runes as one original token. Apostrophes, hyphens, and other punctuation
/// split tokens — <c>"don't"</c> becomes <c>"don"</c> + <c>"t"</c> (the
/// latter dropped by the length filter). Proper UAX #29 word segmentation
/// (which keeps <c>"don't"</c> together) is left to a future analyzer.</para>
/// </remarks>
internal sealed class SimpleEnglishAnalyzer : IFullTextAnalyzer
{
    internal const string AnalyzerName = "simple_en";

    internal const int MinTermLength = 2;

    /// <summary>
    /// Curated stop-word set — common English function words. Kept small so
    /// non-English content surviving through this analyzer isn't gutted, and
    /// so the set fits comfortably in a frozen lookup table.
    /// </summary>
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for",
        "from", "had", "has", "have", "he", "her", "his", "i", "if", "in",
        "is", "it", "its", "me", "my", "no", "not", "of", "on", "or",
        "our", "she", "so", "that", "the", "their", "them", "then", "there",
        "they", "this", "to", "up", "was", "we", "were", "what", "when",
        "which", "who", "will", "with", "would", "you", "your",
    }.ToFrozenSet(StringComparer.Ordinal);

    public string Name => AnalyzerName;

    public IEnumerable<Token> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TokenizeIterator(text);
    }

    private static IEnumerable<Token> TokenizeIterator(string text)
    {
        StringBuilder buffer = new();
        int position = 0;

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                Rune.ToLowerInvariant(rune).AppendTo(buffer);
                continue;
            }

            if (buffer.Length > 0)
            {
                position++;
                string term = buffer.ToString();
                buffer.Clear();

                if (term.Length >= MinTermLength && !StopWords.Contains(term))
                {
                    yield return new Token(term, position);
                }
            }
        }

        if (buffer.Length > 0)
        {
            position++;
            string term = buffer.ToString();

            if (term.Length >= MinTermLength && !StopWords.Contains(term))
            {
                yield return new Token(term, position);
            }
        }
    }
}

file static class RuneExtensions
{
    internal static void AppendTo(this Rune rune, StringBuilder sb)
    {
        Span<char> chars = stackalloc char[2];
        int written = rune.EncodeToUtf16(chars);
        sb.Append(chars[..written]);
    }
}
