using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// <c>mid(string, start, length) → text</c>. Extracts <c>length</c>
/// characters from <c>value</c> starting at 1-based <c>start</c>. Equivalent
/// to <see cref="SubstringFunction"/> with the length argument required.
/// </summary>
/// <remarks>
/// MySQL/Excel-style convenience name; PostgreSQL does not ship a
/// <c>mid()</c>.
/// </remarks>
public sealed class MidFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Extracts a fixed-length substring starting at 1-based start (alias of substring(value, start, length)).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start",  DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("length", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MidFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        string value = args[0].AsString();
        if (!args[1].TryToInt32(out int start))
        {
            throw new FunctionArgumentException(Name, $"argument 'start' of kind {args[1].Kind} is out of range for Int32.");
        }
        if (!args[2].TryToInt32(out int length))
        {
            throw new FunctionArgumentException(Name, $"argument 'length' of kind {args[2].Kind} is out of range for Int32.");
        }
        if (length < 0)
        {
            throw new FunctionArgumentException(Name, "negative length is not allowed.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(SubstringFunction.SubstringCore(value, start, length)));
    }
}
