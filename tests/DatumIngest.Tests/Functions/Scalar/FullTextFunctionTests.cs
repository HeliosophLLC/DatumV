using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Fulltext;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Tests for <see cref="PlainToTsqueryFunction"/> and
/// <see cref="TsqueryMatchFunction"/> — the v1 query constructor (passthrough)
/// and the <c>@@</c> operator backend.
/// </summary>
public sealed class FullTextFunctionTests
{
    // ──────────────────── plainto_tsquery ────────────────────

    [Fact]
    public void PlainToTsquery_Metadata()
    {
        Assert.Equal("plainto_tsquery", PlainToTsqueryFunction.Name);
        Assert.Equal(FunctionCategory.FullText, PlainToTsqueryFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(PlainToTsqueryFunction.Description));
    }

    [Fact]
    public void PlainToTsquery_PassesStringThroughUnchanged()
    {
        // v1 contract: passthrough; tokenization happens inside @@ at eval time.
        ValueRef result = InvokePlain(ValueRef.FromString("the quick brown fox"));
        Assert.Equal("the quick brown fox", result.AsString());
    }

    [Fact]
    public void PlainToTsquery_NullPropagates()
    {
        ValueRef result = InvokePlain(ValueRef.Null(DataKind.String));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public void PlainToTsquery_RegisteredInRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("plainto_tsquery");
        Assert.IsType<PlainToTsqueryFunction>(function);
    }

    // ──────────────────── tsquery_match ────────────────────

    [Fact]
    public void TsqueryMatch_Metadata()
    {
        Assert.Equal("tsquery_match", TsqueryMatchFunction.Name);
        Assert.Equal(FunctionCategory.FullText, TsqueryMatchFunction.Category);
    }

    [Fact]
    public void TsqueryMatch_AllNeedleTermsPresent_ReturnsTrue()
    {
        ValueRef result = InvokeMatch(
            ValueRef.FromString("the quick brown fox jumps"),
            ValueRef.FromString("fox quick"));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_OneNeedleTermMissing_ReturnsFalse()
    {
        ValueRef result = InvokeMatch(
            ValueRef.FromString("the quick brown fox"),
            ValueRef.FromString("fox elephant"));
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_StopWordsInNeedleAreIgnored()
    {
        // Needle "the dog" tokenizes to just "dog" (stop word filtered).
        // Haystack contains "dog" → match.
        ValueRef result = InvokeMatch(
            ValueRef.FromString("the lazy dog"),
            ValueRef.FromString("the dog"));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_EmptyNeedleMatchesEverything()
    {
        // PG semantics: plainto_tsquery('') matches all rows.
        ValueRef result = InvokeMatch(
            ValueRef.FromString("anything"),
            ValueRef.FromString(""));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_NeedleAllStopWords_StillMatches()
    {
        // Needle "the and or" tokenizes to nothing → matches all rows.
        ValueRef result = InvokeMatch(
            ValueRef.FromString("any haystack here"),
            ValueRef.FromString("the and or"));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_CaseInsensitive_ViaAnalyzer()
    {
        // simple_en lowercases both sides.
        ValueRef result = InvokeMatch(
            ValueRef.FromString("Hello WORLD"),
            ValueRef.FromString("HELLO world"));
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void TsqueryMatch_NullHaystack_PropagatesNull()
    {
        ValueRef result = InvokeMatch(
            ValueRef.Null(DataKind.String),
            ValueRef.FromString("foo"));
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    [Fact]
    public void TsqueryMatch_NullNeedle_PropagatesNull()
    {
        ValueRef result = InvokeMatch(
            ValueRef.FromString("foo"),
            ValueRef.Null(DataKind.String));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void TsqueryMatch_RegisteredInRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IScalarFunction? function = registry.TryGetScalar("tsquery_match");
        Assert.IsType<TsqueryMatchFunction>(function);
    }

    private static ValueRef InvokePlain(params ValueRef[] args)
    {
        PlainToTsqueryFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(args, frame, default).GetAwaiter().GetResult();
    }

    private static ValueRef InvokeMatch(params ValueRef[] args)
    {
        TsqueryMatchFunction function = new();
        EvaluationFrame frame = default;
        return function.ExecuteAsync(args, frame, default).GetAwaiter().GetResult();
    }
}
