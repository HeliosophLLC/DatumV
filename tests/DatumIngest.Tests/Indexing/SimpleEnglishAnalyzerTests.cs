using DatumIngest.Indexing.Fts;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="SimpleEnglishAnalyzer"/>: tokenization, lowercasing,
/// stop-word filtering, length filtering, position numbering, and unicode
/// handling.
/// </summary>
public sealed class SimpleEnglishAnalyzerTests
{
    private static readonly SimpleEnglishAnalyzer Analyzer = new();

    [Fact]
    public void Name_IsStableSnakeCase()
    {
        Assert.Equal("simple_en", Analyzer.Name);
    }

    [Fact]
    public void Tokenize_EmptyString_YieldsNothing()
    {
        Assert.Empty(Analyzer.Tokenize(string.Empty));
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_YieldsNothing()
    {
        Assert.Empty(Analyzer.Tokenize("   \t\n\r  "));
    }

    [Fact]
    public void Tokenize_SimpleSentence_YieldsLowercasedTerms()
    {
        Token[] tokens = Analyzer.Tokenize("Hello WORLD foo").ToArray();
        Assert.Equal(new[] { "hello", "world", "foo" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_PunctuationSplitsTokens()
    {
        Token[] tokens = Analyzer.Tokenize("error:timeout!fatal,critical").ToArray();
        Assert.Equal(new[] { "error", "timeout", "fatal", "critical" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_ApostropheSplitsTokens()
    {
        // Documenting the v1 limitation: "don't" → "don" (and "t" dropped by length filter).
        Token[] tokens = Analyzer.Tokenize("don't stop").ToArray();
        Assert.Equal(new[] { "don", "stop" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_FiltersStopWords()
    {
        Token[] tokens = Analyzer.Tokenize("the quick brown fox jumps over the lazy dog").ToArray();
        Assert.Equal(new[] { "quick", "brown", "fox", "jumps", "over", "lazy", "dog" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_FiltersSingleCharacterTokens()
    {
        Token[] tokens = Analyzer.Tokenize("x y zz aaa").ToArray();
        Assert.Equal(new[] { "zz", "aaa" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_Positions_AreOneBasedAndCountStopWords()
    {
        // "the" is stop-word at original position 1; "cat" surfaces at position 2.
        // Stop "the" at position 3 leaves a gap; "dog" surfaces at position 4.
        Token[] tokens = Analyzer.Tokenize("the cat the dog").ToArray();

        Assert.Equal(2, tokens.Length);
        Assert.Equal(("cat", 2), (tokens[0].Term, tokens[0].Position));
        Assert.Equal(("dog", 4), (tokens[1].Term, tokens[1].Position));
    }

    [Fact]
    public void Tokenize_Positions_CountFilteredShortTokens()
    {
        // "x" is filtered for length; "cat" surfaces at position 2.
        Token[] tokens = Analyzer.Tokenize("x cat y dog").ToArray();

        Assert.Equal(2, tokens.Length);
        Assert.Equal(("cat", 2), (tokens[0].Term, tokens[0].Position));
        Assert.Equal(("dog", 4), (tokens[1].Term, tokens[1].Position));
    }

    [Fact]
    public void Tokenize_AlphanumericTokensKeptIntact()
    {
        Token[] tokens = Analyzer.Tokenize("error404 http2 v1").ToArray();
        Assert.Equal(new[] { "error404", "http2", "v1" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_UnicodeLetters_PreservedAndLowercased()
    {
        Token[] tokens = Analyzer.Tokenize("Café Naïve résumé").ToArray();
        Assert.Equal(new[] { "café", "naïve", "résumé" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_CJKCharacters_TreatedAsLetters()
    {
        // Rune.IsLetterOrDigit returns true for CJK ideographs; each character is a letter,
        // so a run of CJK letters becomes one token. Not ideal (CJK wants per-character
        // segmentation), but documents v1 behaviour — a CJK-aware analyzer is a follow-up.
        Token[] tokens = Analyzer.Tokenize("你好 世界").ToArray();
        Assert.Equal(new[] { "你好", "世界" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_EmojiSplitsTokens()
    {
        // Emoji are non-letter-non-digit runes; they split tokens like punctuation.
        Token[] tokens = Analyzer.Tokenize("hello 🎉 world").ToArray();
        Assert.Equal(new[] { "hello", "world" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_TrailingTokenWithoutTerminator_StillEmitted()
    {
        Token[] tokens = Analyzer.Tokenize("alpha beta gamma").ToArray();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, tokens.Select(t => t.Term));
    }

    [Fact]
    public void Tokenize_AllStopWords_YieldsNothing()
    {
        Assert.Empty(Analyzer.Tokenize("the and or but a an"));
    }

    [Fact]
    public void Tokenize_StopWordsAreCaseFoldedBeforeLookup()
    {
        // "THE" must still be recognised as a stop word after lowercasing.
        Assert.Empty(Analyzer.Tokenize("THE AND OR"));
    }

    [Fact]
    public void Tokenize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Analyzer.Tokenize(null!));
    }
}
