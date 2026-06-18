using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>make_timestamp(year, month, day, hour, min, sec)</c> — builds a
/// <see cref="DataKind.Timestamp"/> (no time zone) from integer year/month/
/// day/hour/min plus a fractional <c>sec</c>. Matches PG's signature and
/// return kind; pair with <c>AT TIME ZONE</c> or a future <c>make_timestamptz</c>
/// for zone-aware construction.
/// </summary>
public sealed class MakeTimestampFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "make_timestamp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Builds a Timestamp from year/month/day/hour/minute/second components.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("year",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("month", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("day",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("hour",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("min",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sec",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MakeTimestampFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Timestamp));
            }
        }

        int year   = args[0].TryToInt32(out int y) ? y : 0;
        int month  = args[1].TryToInt32(out int mo) ? mo : 0;
        int day    = args[2].TryToInt32(out int d) ? d : 0;
        int hour   = args[3].TryToInt32(out int h) ? h : 0;
        int minute = args[4].TryToInt32(out int mi) ? mi : 0;
        double sec = args[5].TryToDouble(out double s) ? s : 0.0;

        // Split sec into whole-seconds + fractional ticks so we land at the
        // tick precision DateTime supports (100ns granularity).
        int wholeSeconds = (int)System.Math.Floor(sec);
        double frac = sec - wholeSeconds;
        long fracTicks = (long)System.Math.Round(frac * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);

        try
        {
            DateTime dt = new(year, month, day, hour, minute, wholeSeconds, DateTimeKind.Unspecified);
            if (fracTicks != 0)
            {
                dt = dt.AddTicks(fracTicks);
            }
            return new ValueTask<ValueRef>(ValueRef.FromTimestamp(dt));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ExecutionException(
                $"make_timestamp: invalid components ({year:D4}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{sec}).", ex);
        }
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
