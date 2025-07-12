namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.Functions;
using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="FunctionDocumentation"/> — the static registry of
/// built-in function signatures for language server autocomplete and hover.
/// </summary>
public sealed class FunctionDocumentationTests
{
    // ───────────────────── Lookup ─────────────────────

    [Theory]
    [InlineData("abs")]
    [InlineData("sqrt")]
    [InlineData("sigmoid")]
    [InlineData("cosine_similarity")]
    [InlineData("len")]
    [InlineData("json_value")]
    [InlineData("load_image")]
    [InlineData("resize")]
    [InlineData("unnest")]
    [InlineData("range")]
    public void TryGet_KnownFunction_ReturnsSignature(string functionName)
    {
        FunctionSignature? signature = FunctionDocumentation.TryGet(functionName);

        Assert.NotNull(signature);
        Assert.Equal(functionName, signature.Name, ignoreCase: true);
    }

    [Fact]
    public void TryGet_UnknownFunction_ReturnsNull()
    {
        FunctionSignature? signature = FunctionDocumentation.TryGet("not_a_real_function");

        Assert.Null(signature);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        FunctionSignature? lower = FunctionDocumentation.TryGet("abs");
        FunctionSignature? upper = FunctionDocumentation.TryGet("ABS");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.Equal(lower.Name, upper.Name);
    }

    // ───────────────────── All() coverage ─────────────────────

    [Fact]
    public void All_ContainsDocumentedFunctions()
    {
        List<FunctionSignature> all = FunctionDocumentation.All.ToList();

        Assert.True(all.Count > 100, $"Expected >100 documented functions, got {all.Count}.");
    }

    [Fact]
    public void All_EachEntry_HasNameAndParameters()
    {
        foreach (FunctionSignature function in FunctionDocumentation.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Name),
                "Every function must have a non-empty name.");
            Assert.NotNull(function.Parameters);
        }
    }

    [Fact]
    public void All_EachEntry_HasDescription()
    {
        foreach (FunctionSignature function in FunctionDocumentation.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Description),
                $"Function '{function.Name}' is missing a description.");
        }
    }

    // ───────────────────── Table-valued functions ─────────────────────

    [Fact]
    public void TableValuedFunctions_AreMarkedCorrectly()
    {
        FunctionSignature? unnest = FunctionDocumentation.TryGet("unnest");
        FunctionSignature? range = FunctionDocumentation.TryGet("range");

        Assert.NotNull(unnest);
        Assert.True(unnest.IsTableValued);

        Assert.NotNull(range);
        Assert.True(range.IsTableValued);
    }

    [Fact]
    public void ScalarFunctions_AreNotMarkedTableValued()
    {
        FunctionSignature? abs = FunctionDocumentation.TryGet("abs");

        Assert.NotNull(abs);
        Assert.False(abs.IsTableValued);
    }

    // ───────────────────── Parameter signatures ─────────────────────

    [Fact]
    public void FunctionWithOptionalParameter_MarksParameterCorrectly()
    {
        FunctionSignature? leakyRelu = FunctionDocumentation.TryGet("leaky_relu");

        Assert.NotNull(leakyRelu);
        Assert.True(leakyRelu.Parameters.Count >= 2);
        Assert.False(leakyRelu.Parameters[0].IsOptional);
        Assert.True(leakyRelu.Parameters[1].IsOptional);
    }

    [Fact]
    public void ZeroArgFunction_HasEmptyParameters()
    {
        FunctionSignature? pi = FunctionDocumentation.TryGet("pi");

        Assert.NotNull(pi);
        Assert.Empty(pi.Parameters);
    }
}
