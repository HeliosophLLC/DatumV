using DatumIngest.Functions;

namespace DatumIngest.Tests.Functions.Math;

/// <summary>
/// Verifies that all 88 math functions are properly registered in the default FunctionRegistry.
/// </summary>
public class MathFunctionRegistrationTests
{
    private readonly FunctionRegistry _registry = FunctionRegistry.CreateDefault();

    [Theory]
    [InlineData("abs")]
    [InlineData("sign")]
    [InlineData("negate")]
    [InlineData("mod")]
    [InlineData("add")]
    [InlineData("subtract")]
    [InlineData("multiply")]
    [InlineData("divide")]
    [InlineData("sqrt")]
    [InlineData("cbrt")]
    [InlineData("square")]
    [InlineData("exp")]
    [InlineData("exp2")]
    [InlineData("ln")]
    [InlineData("log2")]
    [InlineData("log10")]
    [InlineData("pow")]
    [InlineData("log")]
    [InlineData("sin")]
    [InlineData("cos")]
    [InlineData("tan")]
    [InlineData("asin")]
    [InlineData("acos")]
    [InlineData("atan")]
    [InlineData("atan2")]
    [InlineData("sinh")]
    [InlineData("cosh")]
    [InlineData("tanh")]
    [InlineData("degrees")]
    [InlineData("radians")]
    [InlineData("pi")]
    [InlineData("euler")]
    [InlineData("ceil")]
    [InlineData("floor")]
    [InlineData("truncate")]
    [InlineData("round")]
    [InlineData("quantize")]
    [InlineData("bucketize")]
    [InlineData("clip")]
    [InlineData("sigmoid")]
    [InlineData("relu")]
    [InlineData("selu")]
    [InlineData("gelu")]
    [InlineData("swish")]
    [InlineData("softplus")]
    [InlineData("softsign")]
    [InlineData("mish")]
    [InlineData("hard_sigmoid")]
    [InlineData("hard_swish")]
    [InlineData("leaky_relu")]
    [InlineData("elu")]
    [InlineData("softmax")]
    [InlineData("log_softmax")]
    [InlineData("l2_normalize")]
    [InlineData("vec_sum")]
    [InlineData("vec_mean")]
    [InlineData("vec_min")]
    [InlineData("vec_max")]
    [InlineData("vec_std")]
    [InlineData("vec_var")]
    [InlineData("vec_median")]
    [InlineData("vec_argmin")]
    [InlineData("vec_argmax")]
    [InlineData("vec_norm")]
    [InlineData("vec_count_nonzero")]
    [InlineData("vec_any")]
    [InlineData("vec_all")]
    [InlineData("vec_product")]
    [InlineData("vec_slice")]
    [InlineData("vec_concat")]
    [InlineData("vec_reverse")]
    [InlineData("vec_sort")]
    [InlineData("vec_unique")]
    [InlineData("vec_flatten")]
    [InlineData("vec_pad")]
    [InlineData("vec_repeat")]
    [InlineData("linspace")]
    [InlineData("arange")]
    [InlineData("cosine_similarity")]
    [InlineData("euclidean_distance")]
    [InlineData("manhattan_distance")]
    [InlineData("dot")]
    [InlineData("hamming_distance")]
    [InlineData("coalesce")]
    [InlineData("greatest")]
    [InlineData("least")]
    [InlineData("is_nan")]
    [InlineData("is_finite")]
    [InlineData("if_null")]
    [InlineData("random")]
    public void Function_IsRegistered(string name)
    {
        Assert.NotNull(_registry.TryGetScalar(name));
    }

    [Fact]
    public void AllMathFunctions_Count_Is90()
    {
        string[] mathFunctions =
        [
            "abs", "sign", "negate", "mod", "add", "subtract", "multiply", "divide",
            "sqrt", "cbrt", "square", "exp", "exp2", "ln", "log2", "log10", "pow", "log",
            "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sinh", "cosh", "tanh",
            "degrees", "radians", "pi", "euler",
            "ceil", "floor", "truncate", "round", "quantize", "bucketize", "clip",
            "sigmoid", "relu", "selu", "gelu", "swish", "softplus", "softsign", "mish",
            "hard_sigmoid", "hard_swish", "leaky_relu", "elu",
            "softmax", "log_softmax", "l2_normalize",
            "vec_sum", "vec_mean", "vec_min", "vec_max", "vec_std", "vec_var", "vec_median",
            "vec_argmin", "vec_argmax", "vec_norm", "vec_count_nonzero", "vec_any", "vec_all", "vec_product",
            "vec_slice", "vec_concat", "vec_reverse", "vec_sort", "vec_unique", "vec_flatten",
            "vec_pad", "vec_repeat", "linspace", "arange",
            "cosine_similarity", "euclidean_distance", "manhattan_distance", "dot", "hamming_distance",
            "coalesce", "greatest", "least", "is_nan", "is_finite", "if_null", "random"
        ];

        Assert.Equal(90, mathFunctions.Length);
        foreach (string name in mathFunctions)
        {
            Assert.NotNull(_registry.TryGetScalar(name));
        }
    }
}
