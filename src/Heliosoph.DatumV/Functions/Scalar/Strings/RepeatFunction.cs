using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>repeat(string, count) → text</c>. Concatenates <c>count</c>
/// copies of <c>value</c>. A non-positive <c>count</c> returns an empty
/// string. Null in any argument propagates to null.
/// </summary>
public sealed class RepeatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "repeat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the string repeated count times (non-positive count yields the empty string).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RepeatFunction>(argumentKinds);

    // Guards against malicious or accidental memory blow-up. PG itself caps
    // result strings at 1 GB; we cap at 16 MB which covers any realistic use.
    private const int MaxResultLength = 16 * 1024 * 1024;

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
        if (!args[1].TryToInt32(out int count))
        {
            throw new FunctionArgumentException(Name, $"argument 'count' of kind {args[1].Kind} is out of range for Int32.");
        }
        if (count <= 0 || value.Length == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromString(""));
        }

        long totalLength = (long)value.Length * count;
        if (totalLength > MaxResultLength)
        {
            throw new FunctionArgumentException(Name,
                $"repeated string would exceed the {MaxResultLength}-character limit.");
        }

        StringBuilder sb = new((int)totalLength);
        for (int i = 0; i < count; i++)
        {
            sb.Append(value);
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(sb.ToString()));
    }
}
