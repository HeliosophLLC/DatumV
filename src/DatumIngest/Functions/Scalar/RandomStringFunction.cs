using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns one of the supplied strings selected uniformly at random.
/// Requires two or more arguments. Null arguments are eligible for
/// selection — a returned null carries the <see cref="DataKind.String"/>
/// kind. Use <c>random_string_from_seed</c> for a deterministic variant.
/// </summary>
/// <remarks>
/// <para>
/// Non-deterministic by design: <see cref="IsPure"/> is
/// <see langword="false"/>, which keeps common-subexpression elimination
/// from collapsing two textual references into a single evaluation.
/// </para>
/// </remarks>
public sealed class RandomStringFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "random_string";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns one of the supplied strings selected uniformly at random. Requires two or more arguments.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "values",
                DataKindMatcher.Exact(DataKind.String),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RandomStringFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        int index = Random.Shared.Next(arguments.Length);
        return arguments[index];
    }

    /// <inheritdoc />
    public bool IsPure => false;
}
