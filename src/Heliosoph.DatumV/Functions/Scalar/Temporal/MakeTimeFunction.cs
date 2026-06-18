using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>make_time(hour, min, sec)</c> — builds a <see cref="DataKind.Time"/>
/// from integer hour / min plus a fractional <c>sec</c>.
/// </summary>
public sealed class MakeTimeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "make_time";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Builds a Time from hour/minute/second components.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("hour", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sec",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Time)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MakeTimeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Time));
        }

        int hour   = args[0].TryToInt32(out int h) ? h : 0;
        int minute = args[1].TryToInt32(out int mi) ? mi : 0;
        double sec = args[2].TryToDouble(out double s) ? s : 0.0;

        int wholeSeconds = (int)System.Math.Floor(sec);
        double frac = sec - wholeSeconds;
        long fracTicks = (long)System.Math.Round(frac * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);

        try
        {
            TimeOnly t = new(hour, minute, wholeSeconds);
            if (fracTicks != 0)
            {
                t = t.Add(TimeSpan.FromTicks(fracTicks));
            }
            return new ValueTask<ValueRef>(ValueRef.FromTime(t));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ExecutionException(
                $"make_time: invalid components ({hour:D2}:{minute:D2}:{sec}).", ex);
        }
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
