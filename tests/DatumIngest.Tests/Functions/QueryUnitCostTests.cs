using DatumIngest.Functions;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Verifies that every registered scalar function has a valid Query Unit cost
/// and that representative functions from each tier report the expected cost.
/// </summary>
public class QueryUnitCostTests
{
    /// <summary>
    /// Every scalar function in the default registry must have a positive Query Unit cost.
    /// </summary>
    [Fact]
    public void AllScalarFunctions_HavePositiveQueryUnitCost()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        foreach (string name in registry.ScalarFunctionNames)
        {
            IScalarFunction? function = registry.TryGetScalar(name);
            Assert.NotNull(function);
            Assert.True(function.QueryUnitCost >= 1,
                $"Function '{name}' has QueryUnitCost {function.QueryUnitCost}, expected >= 1.");
        }
    }

    /// <summary>
    /// Representative functions from each cost tier must report the correct Query Unit cost.
    /// </summary>
    [Theory]
    [InlineData("abs", 1)]
    [InlineData("len", 1)]
    [InlineData("cast", 1)]
    [InlineData("date_part", 1)]
    [InlineData("width", 1)]
    [InlineData("cosine_similarity", 2)]
    [InlineData("vec_sum", 2)]
    [InlineData("softmax", 2)]
    [InlineData("vec_sort", 2)]
    [InlineData("json_value", 5)]
    [InlineData("json_exists", 5)]
    [InlineData("detect_blur", 10)]
    [InlineData("image_pixel_mean", 10)]
    [InlineData("perceptual_hash", 10)]
    [InlineData("resize", 50)]
    [InlineData("image_to_bytes", 50)]
    [InlineData("image_to_tensor_hwc", 50)]
    [InlineData("image_to_tensor_chw", 50)]
    [InlineData("grayscale", 50)]
    public void Function_ReportsExpectedQueryUnitCost(string functionName, int expectedCost)
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        IScalarFunction? function = registry.TryGetScalar(functionName);
        Assert.NotNull(function);
        Assert.Equal(expectedCost, function.QueryUnitCost);
    }
}
