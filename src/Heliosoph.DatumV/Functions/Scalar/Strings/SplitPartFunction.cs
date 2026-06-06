using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>split_part(string, delimiter, n) → text</c>. Splits
/// <c>value</c> on the literal <c>delimiter</c> and returns the n-th field
/// (1-based). Negative <c>n</c> counts back from the end (-1 is the last
/// field). Returns the empty string when <c>n</c> is out of range or when
/// <c>n</c> is zero. Null in any argument propagates to null.
/// </summary>
public sealed class SplitPartFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "split_part";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Splits value on delimiter and returns the n-th field (1-based; negative n counts from end).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("n",         DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SplitPartFunction>(argumentKinds);

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
        string delim = args[1].AsString();
        if (!args[2].TryToInt32(out int n))
        {
            throw new FunctionArgumentException(Name, $"argument 'n' of kind {args[2].Kind} is out of range for Int32.");
        }

        // PG: empty delimiter — n=1 yields the whole string; n=-1 also yields
        // the whole string (single field, indexable from either end); any
        // other n is out of range.
        if (delim.Length == 0)
        {
            string only = (n == 1 || n == -1) ? value : "";
            return new ValueTask<ValueRef>(ValueRef.FromString(only));
        }

        if (n == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(""));
        }

        string[] parts = value.Split(delim, StringSplitOptions.None);
        int idx = n > 0 ? n - 1 : parts.Length + n;
        if (idx < 0 || idx >= parts.Length)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(""));
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(parts[idx]));
    }
}
