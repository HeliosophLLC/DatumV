using DatumIngest.Functions;

namespace DatumIngest.Tests.Functions.Scalar;

/// <summary>
/// Asserts the registered scalar function set matches the rebuild's
/// expected list. Turns "I forgot to register X" into a clear failure
/// instead of an obscure "no such function" at query time. Update
/// <see cref="ExpectedScalarFunctions"/> in lockstep with stages 4–6
/// of the function rebuild.
/// </summary>
public sealed class RegisteredScalarFunctionsTests
{
    /// <summary>
    /// Canonical list of scalar functions the rebuild has delivered so far.
    /// Stage 4 added <c>concat</c>; later stages will append <c>upper</c>,
    /// <c>lower</c>, <c>cast</c>, <c>try_cast</c>, <c>typeof</c>.
    /// </summary>
    private static readonly string[] ExpectedScalarFunctions =
    [
        "concat",
        "upper",
        "lower",
        "cast",
        "try_cast",
        "typeof",
        "abs",
        "random_string",
        "random_string_from_seed",
        "random_float32",
        "random_float32_from_seed",
        "json_parse",
        "json_value",
        "json_query",
        "json_to_text",
    ];

    [Fact]
    public void RegistrySet_MatchesExpectedList()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        HashSet<string> actual = new(registry.ScalarFunctionNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> expected = new(ExpectedScalarFunctions, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> unexpected = actual.Except(expected, StringComparer.OrdinalIgnoreCase);

        string missingList = string.Join(", ", missing);
        string unexpectedList = string.Join(", ", unexpected);
        Assert.True(
            actual.SetEquals(expected),
            $"Registered scalar functions diverge from expected.\n"
            + $"  Missing (declared but not registered): [{missingList}]\n"
            + $"  Unexpected (registered but not declared): [{unexpectedList}]\n"
            + "If you intentionally added or removed a function, update "
            + "ExpectedScalarFunctions in this test.");
    }
}
