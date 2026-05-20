using Heliosoph.DatumV.Indexing.Fts;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests for <see cref="FtsAnalyzerRegistry"/>: lookup, case-insensitive
/// name matching, default content, error shape on miss, duplicate-name
/// rejection at construction.
/// </summary>
public sealed class FtsAnalyzerRegistryTests
{
    [Fact]
    public void Default_Contains_SimpleEnglishAnalyzer()
    {
        IFullTextAnalyzer analyzer = FtsAnalyzerRegistry.Default.Get("simple_en");
        Assert.IsType<SimpleEnglishAnalyzer>(analyzer);
    }

    [Fact]
    public void Get_NameCaseInsensitive()
    {
        IFullTextAnalyzer a1 = FtsAnalyzerRegistry.Default.Get("SIMPLE_EN");
        IFullTextAnalyzer a2 = FtsAnalyzerRegistry.Default.Get("Simple_En");
        Assert.Same(a1, a2);
    }

    [Fact]
    public void Get_UnknownName_Throws_WithRegisteredNamesInMessage()
    {
        FtsAnalyzerNotFoundException ex = Assert.Throws<FtsAnalyzerNotFoundException>(
            () => FtsAnalyzerRegistry.Default.Get("porter_en"));

        Assert.Equal("porter_en", ex.AnalyzerName);
        Assert.Contains("simple_en", ex.Message);
        Assert.Contains("porter_en", ex.Message);
    }

    [Fact]
    public void TryGet_KnownName_ReturnsTrue()
    {
        Assert.True(FtsAnalyzerRegistry.Default.TryGet("simple_en", out IFullTextAnalyzer? analyzer));
        Assert.NotNull(analyzer);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsFalse()
    {
        Assert.False(FtsAnalyzerRegistry.Default.TryGet("nonexistent", out IFullTextAnalyzer? analyzer));
        Assert.Null(analyzer);
    }

    [Fact]
    public void RegisteredNames_IncludesDefaultAnalyzer()
    {
        Assert.Contains("simple_en", FtsAnalyzerRegistry.Default.RegisteredNames);
    }

    [Fact]
    public void Ctor_DuplicateName_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            new FtsAnalyzerRegistry(new IFullTextAnalyzer[]
            {
                new SimpleEnglishAnalyzer(),
                new SimpleEnglishAnalyzer(),
            }));

        Assert.Contains("simple_en", ex.Message);
    }

    [Fact]
    public void Ctor_CustomAnalyzerSet_Registered()
    {
        FtsAnalyzerRegistry registry = new(new IFullTextAnalyzer[] { new FakeAnalyzer("fake") });

        Assert.True(registry.TryGet("fake", out _));
        Assert.False(registry.TryGet("simple_en", out _));
    }

    private sealed class FakeAnalyzer(string name) : IFullTextAnalyzer
    {
        public string Name { get; } = name;
        public IEnumerable<Token> Tokenize(string text) => Array.Empty<Token>();
    }
}
