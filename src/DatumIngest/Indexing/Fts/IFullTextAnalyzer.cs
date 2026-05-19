namespace Heliosoph.DatumV.Indexing.Fts;

/// <summary>
/// Tokenizes a string into a sequence of <see cref="Token"/>s suitable for
/// indexing in an inverted full-text index. Implementations are stateless;
/// the same analyzer instance is reused across calls and threads.
/// </summary>
/// <remarks>
/// <para>The analyzer's <see cref="Name"/> is persisted in the index sidecar
/// and the per-table manifest; opening an index whose analyzer name is no
/// longer registered in <see cref="FtsAnalyzerRegistry"/> is a hard error.
/// Adding a new analyzer is therefore an additive, non-breaking change as
/// long as the name stays stable.</para>
/// </remarks>
public interface IFullTextAnalyzer
{
    /// <summary>
    /// Stable, lowercase, snake_case identifier (e.g. <c>"simple_en"</c>).
    /// Persisted in the manifest; never change the name of an existing
    /// analyzer or pre-existing indexes will fail to open.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Tokenizes <paramref name="text"/>, yielding one <see cref="Token"/>
    /// per surviving lexeme. Stop words and short tokens are filtered;
    /// filtered tokens still consume a position number in the surviving
    /// tokens' <see cref="Token.Position"/> values so that future phrase
    /// queries can detect adjacency gaps.
    /// </summary>
    IEnumerable<Token> Tokenize(string text);
}

/// <summary>
/// A single lexeme emitted by an analyzer. <see cref="Term"/> is the
/// post-fold, post-filter form that goes into the inverted index;
/// <see cref="Position"/> is the 1-based ordinal of the original token in
/// the source text (filtered tokens consume positions, so a stop word
/// between two surviving tokens leaves a gap of 2 in their positions).
/// </summary>
/// <remarks>
/// v1 of the FTS index does not persist <see cref="Position"/>; it is
/// retained on the contract so v2 phrase queries can drop in without
/// reshaping the analyzer interface.
/// </remarks>
public readonly record struct Token(string Term, int Position);
