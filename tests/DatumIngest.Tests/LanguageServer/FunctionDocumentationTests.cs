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

    // ───────────────────── Category ─────────────────────

    [Theory]
    [InlineData("abs", FunctionCategory.Numeric)]
    [InlineData("sqrt", FunctionCategory.Numeric)]
    [InlineData("pi", FunctionCategory.Numeric)]
    [InlineData("len", FunctionCategory.String)]
    [InlineData("get_filename", FunctionCategory.String)]
    [InlineData("make_date", FunctionCategory.Temporal)]
    [InlineData("year", FunctionCategory.Temporal)]
    [InlineData("strftime", FunctionCategory.Temporal)]
    [InlineData("sigmoid", FunctionCategory.Activation)]
    [InlineData("softmax", FunctionCategory.Activation)]
    [InlineData("l2_normalize", FunctionCategory.Activation)]
    [InlineData("cosine_similarity", FunctionCategory.Vector)]
    [InlineData("vec_sum", FunctionCategory.Vector)]
    [InlineData("reshape", FunctionCategory.Vector)]
    [InlineData("resize", FunctionCategory.Image)]
    [InlineData("load_image", FunctionCategory.Image)]
    [InlineData("perceptual_hash", FunctionCategory.Image)]
    [InlineData("json_value", FunctionCategory.Json)]
    [InlineData("sha256", FunctionCategory.Encoding)]
    [InlineData("base64_encode", FunctionCategory.Encoding)]
    [InlineData("uuidv4", FunctionCategory.Encoding)]
    [InlineData("cast", FunctionCategory.Conversion)]
    [InlineData("coalesce", FunctionCategory.Utility)]
    [InlineData("unnest", FunctionCategory.Table)]
    [InlineData("range", FunctionCategory.Table)]
    public void KnownFunction_HasExpectedCategory(string functionName, FunctionCategory expectedCategory)
    {
        FunctionSignature? signature = FunctionDocumentation.TryGet(functionName);

        Assert.NotNull(signature);
        Assert.Equal(expectedCategory, signature.Category);
    }

    [Fact]
    public void AllCategories_AreRepresented()
    {
        HashSet<FunctionCategory> categoriesPresent = FunctionDocumentation.All
            .Select(function => function.Category)
            .ToHashSet();

        foreach (FunctionCategory category in Enum.GetValues<FunctionCategory>())
        {
            Assert.Contains(category, categoriesPresent);
        }
    }

    // ───────────────────── Cross-registry consistency ─────────────────────

    /// <summary>
    /// Every name in <see cref="FunctionDocumentation"/> must match a function
    /// registered in <see cref="FunctionRegistry"/>. Catches stale or misspelled
    /// documentation keys such as the prior "brightness_histogram" vs
    /// "image_brightness_histogram" mismatch.
    /// </summary>
    [Fact]
    public void AllDocumentedNames_ExistInFunctionRegistry()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        HashSet<string> registeredNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in registry.ScalarFunctionNames) registeredNames.Add(name);
        foreach (string name in registry.TableValuedFunctionNames) registeredNames.Add(name);
        foreach (string name in registry.AggregateFunctionNames) registeredNames.Add(name);
        foreach (string name in registry.WindowFunctionNames) registeredNames.Add(name);

        List<string> undocumentedNames = FunctionDocumentation.All
            .Select(function => function.Name)
            .Where(name => !registeredNames.Contains(name))
            .OrderBy(name => name)
            .ToList();

        Assert.Empty(undocumentedNames);
    }

    /// <summary>
    /// Every function registered in <see cref="FunctionRegistry"/> should have an
    /// entry in <see cref="FunctionDocumentation"/>. Catches functions added to the
    /// registry that were never documented.
    /// </summary>
    [Fact]
    public void AllRegisteredFunctions_HaveDocumentation()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        IEnumerable<string> allRegisteredNames = registry.ScalarFunctionNames
            .Concat(registry.TableValuedFunctionNames)
            .Concat(registry.AggregateFunctionNames)
            .Concat(registry.WindowFunctionNames);

        List<string> undocumented = allRegisteredNames
            .Where(name => FunctionDocumentation.TryGet(name) is null)
            .OrderBy(name => name)
            .ToList();

        Assert.Empty(undocumented);
    }
}
