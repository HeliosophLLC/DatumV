using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Returns the 1-based index of the first occurrence of <c>substring</c> in
/// <c>value</c>, or 0 if not found. Aliased as <c>strpos</c>. Null in any
/// argument propagates to null. Comparison is ordinal (byte-wise on the
/// underlying UTF-16 sequence).
/// </summary>
/// <remarks>
/// PostgreSQL exposes two surface forms: the parser-level
/// <c>POSITION(substring IN string)</c> and the function-call <c>strpos</c>.
/// This is the function-call form with arguments ordered as <c>(string,
/// substring)</c>, matching <c>strpos</c>.
/// </remarks>
public sealed class PositionFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "position";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the 1-based index of the first occurrence of substring in value, or 0 if not found.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("substring", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PositionFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        string value = args[0].AsString();
        string sub = args[1].AsString();

        // PG: empty substring matches at position 1.
        if (sub.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(1));
        }

        int idx = value.IndexOf(sub, StringComparison.Ordinal);
        return new ValueTask<ValueRef>(ValueRef.FromInt32(idx < 0 ? 0 : idx + 1));
    }
}
