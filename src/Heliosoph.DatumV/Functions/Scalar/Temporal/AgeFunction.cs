using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>age(later, earlier)</c> — calendar-aware difference that returns an
/// <see cref="DataKind.Interval"/> with independent year / month / day /
/// sub-day fields. The "how old / how long ago" function, distinct from the
/// raw elapsed-time form <c>later - earlier</c> (which returns
/// <see cref="DataKind.Duration"/>).
/// </summary>
public sealed class AgeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "age";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Returns a calendar-aware Interval (years/months/days/sub-day) between two timestamps.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("later",   DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("earlier", DataKindMatcher.Exact(DataKind.Timestamp)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("later",   DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("earlier", DataKindMatcher.Exact(DataKind.TimestampTz)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AgeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval));
        }

        Interval result = args[0].Kind switch
        {
            DataKind.Timestamp => Interval.Age(args[0].AsTimestamp(), args[1].AsTimestamp()),
            DataKind.TimestampTz => Interval.Age(args[0].AsTimestampTz(), args[1].AsTimestampTz()),
            _ => throw new ExecutionException(
                $"age: unsupported source kind {args[0].Kind}."),
        };
        return new ValueTask<ValueRef>(ValueRef.FromInterval(result));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
