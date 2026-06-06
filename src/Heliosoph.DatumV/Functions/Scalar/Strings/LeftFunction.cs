using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>left(string, n) → text</c>. Returns the first <c>n</c>
/// characters when <c>n</c> is non-negative. When <c>n</c> is negative,
/// returns all but the last <c>|n|</c> characters (empty if <c>|n|</c>
/// exceeds the length). Null in any argument propagates to null.
/// </summary>
public sealed class LeftFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "left";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the first n characters; negative n returns all but the last |n| characters.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("n",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LeftFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        string value = args[0].AsString();
        if (!args[1].TryToInt32(out int n))
        {
            throw new FunctionArgumentException(Name, $"argument 'n' of kind {args[1].Kind} is out of range for Int32.");
        }
        int take = n >= 0 ? System.Math.Min(n, value.Length) : System.Math.Max(0, value.Length + n);
        return new ValueTask<ValueRef>(ValueRef.FromString(value[..take]));
    }
}
