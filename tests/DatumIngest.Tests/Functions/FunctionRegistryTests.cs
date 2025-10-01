using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions;

public class FunctionRegistryTests
{
    [Fact]
    public void CreateDefault_RegistersAllScalarFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        string[] expectedNames =
        [
            "min_max_normalize", "clamp", "denormalize", "reshape",
            "len", "mid", "substring",
            "get_filename", "get_file_extension", "get_path",
            "cast", "to_epoch", "date_part", "cyclical_encode",
            "json_value", "json_query", "json_exists", "json_array_length",
            "width", "height", "channels", "pixel_count", "dimensions",
            "image_to_bytes", "image_to_tensor_hwc", "image_to_tensor_chw",
            "resize", "crop", "grayscale", "rotate", "noise", "blur",
            "year", "month", "day", "hour", "minute", "second",
            "quarter", "dayofweek", "dayofyear",
            "now", "make_date", "make_timestamp",
            "date_diff", "date_add", "date_trunc", "date_bucket",
            "strftime", "is_date",
        ];

        foreach (string name in expectedNames)
        {
            Assert.NotNull(registry.TryGetScalar(name));
        }
    }

    [Fact]
    public void CreateDefault_RegistersTableValuedFunctions()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.NotNull(registry.TryGetTableValued("unnest"));
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.NotNull(registry.TryGetScalar("NORMALIZE"));
        Assert.NotNull(registry.TryGetScalar("Clamp"));
        Assert.NotNull(registry.TryGetTableValued("UNNEST"));
    }

    [Fact]
    public void TryGetScalar_ReturnsNullForUnknown()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.Null(registry.TryGetScalar("nonexistent"));
    }

    [Fact]
    public void TryGetTableValued_ReturnsNullForUnknown()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        Assert.Null(registry.TryGetTableValued("nonexistent"));
    }

    [Fact]
    public void RegisterScalar_ThrowsOnDuplicate()
    {
        FunctionRegistry registry = new();
        registry.RegisterScalar(new MinMaxNormalizeFunction());
        Assert.Throws<ArgumentException>(() => registry.RegisterScalar(new MinMaxNormalizeFunction()));
    }

    [Fact]
    public void ScalarFunctionNames_ListsRegistered()
    {
        FunctionRegistry registry = new();
        registry.RegisterScalar(new LenFunction());
        registry.RegisterScalar(new ClampFunction());
        Assert.Contains("len", registry.ScalarFunctionNames);
        Assert.Contains("clamp", registry.ScalarFunctionNames);
    }

    [Fact]
    public void TableValuedFunctionNames_ListsRegistered()
    {
        FunctionRegistry registry = new();
        registry.RegisterTableValued(new UnnestFunction());
        Assert.Contains("unnest", registry.TableValuedFunctionNames);
    }
}
