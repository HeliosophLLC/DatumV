using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// <c>make_duration(days, hours, minutes, seconds)</c> — builds a
/// <see cref="DataKind.Duration"/> (fixed elapsed-time span) from numeric
/// components. DatumV-specific; the calendar-aware sibling is
/// <c>make_interval</c>. Use Duration when you need elapsed wall time
/// (a result of <c>ts - ts</c>); use Interval when you need months / years
/// that respect calendar boundaries.
/// </summary>
public sealed class MakeDurationFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "make_duration";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Builds a Duration from day/hour/minute/second components.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("days",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("hours",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("minutes", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("seconds", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Duration)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MakeDurationFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Duration));
        }

        double days    = args[0].TryToDouble(out double d) ? d : 0.0;
        double hours   = args[1].TryToDouble(out double h) ? h : 0.0;
        double minutes = args[2].TryToDouble(out double m) ? m : 0.0;
        double seconds = args[3].TryToDouble(out double s) ? s : 0.0;

        double totalSeconds =
            days * 86400.0
            + hours * 3600.0
            + minutes * 60.0
            + seconds;
        long ticks = (long)System.Math.Round(totalSeconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
        return new ValueTask<ValueRef>(ValueRef.FromDuration(TimeSpan.FromTicks(ticks)));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
