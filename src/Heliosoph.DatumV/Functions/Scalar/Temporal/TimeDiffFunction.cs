using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// <c>time_diff(start, end)</c> — Duration between two <see cref="DataKind.Time"/>
/// values, wrapping forward through midnight. So
/// <c>time_diff(23:00, 02:00) = 3 hours</c>, not −21 hours. Use this in
/// preference to <c>end − start</c> when modelling overnight shifts.
/// </summary>
public sealed class TimeDiffFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "time_diff";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Duration between two Time values, wrapping forward through midnight.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("start", DataKindMatcher.Exact(DataKind.Time)),
                new ParameterSpec("end",   DataKindMatcher.Exact(DataKind.Time)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Duration)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TimeDiffFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Duration));
        }

        TimeOnly start = args[0].AsTime();
        TimeOnly end   = args[1].AsTime();
        // TimeOnly already implements the wrap-forward semantics on subtraction.
        TimeSpan delta = end - start;
        return new ValueTask<ValueRef>(ValueRef.FromDuration(delta));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
