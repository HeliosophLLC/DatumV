using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the first argument verbatim when it is non-null; throws an
/// <see cref="InvalidOperationException"/> with the message from the
/// second argument when it is null. Used by <see cref="UdfInliner"/>
/// to enforce <c>IS NOT NULL</c> on UDF parameters and on declared
/// return values, and available as a normal scalar function for user
/// SQL.
/// </summary>
public sealed class AssertNotNullFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_not_null";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when non-null; throws when null. Used by " +
        "the UDF inliner to enforce IS NOT NULL annotations on parameters " +
        "and return values.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertNotNullFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (!value.IsNull) return new ValueTask<ValueRef>(value);

        // Pull the diagnostic message out of the second argument (always
        // a string literal injected by the inliner). Falls back to a
        // generic message for the (unusual) call-site that hand-types
        // this function with a non-literal message.
        string message = args[1].Kind == DataKind.String && !args[1].IsNull
            ? args[1].AsString()
            : "value is null";
        throw new InvalidOperationException(message);
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
