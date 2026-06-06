using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG-15 <c>date_bin(stride, source, origin)</c> — rounds the source value
/// down to the nearest interval-stride boundary aligned to the origin.
/// Returns the same temporal kind as the source.
/// The bucketer of choice for arbitrary windowing (5-minute bars,
/// 15-second polling, etc.) — calendar-aware <c>date_trunc</c> handles the
/// year / month / day cases.
/// </summary>
/// <remarks>
/// <para>
/// Stride must be expressible as a positive elapsed duration — the months
/// component must be zero, because a "month" has no fixed second count.
/// Days are folded into microseconds at 24h flat.
/// </para>
/// </remarks>
public sealed class DateBinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "date_bin";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Bins a timestamp to the nearest multiple of an interval stride from an origin.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("origin", DataKindMatcher.Exact(DataKind.Timestamp)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("stride", DataKindMatcher.Exact(DataKind.Interval)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("origin", DataKindMatcher.Exact(DataKind.TimestampTz)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.TimestampTz)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DateBinFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(args[1].Kind));
        }

        Interval stride = args[0].AsInterval();
        if (stride.Months != 0)
        {
            throw new ExecutionException(
                "date_bin: stride must not contain a month component (months are " +
                "variable-length). Use date_trunc('month', ...) for month buckets.");
        }
        long strideMicros = checked(stride.Days * Interval.MicrosPerDay + stride.Microseconds);
        if (strideMicros <= 0)
        {
            throw new ExecutionException(
                "date_bin: stride must be a positive interval.");
        }

        long strideTicks = strideMicros * 10L; // µs → ticks

        if (args[1].Kind == DataKind.Timestamp)
        {
            long sourceTicks = args[1].AsTimestamp().Ticks;
            long originTicks = args[2].AsTimestamp().Ticks;
            long bucket = BinTicks(sourceTicks, originTicks, strideTicks);
            return new ValueTask<ValueRef>(
                ValueRef.FromTimestamp(new DateTime(bucket, DateTimeKind.Unspecified)));
        }
        else
        {
            long sourceTicks = args[1].AsTimestampTz().UtcTicks;
            long originTicks = args[2].AsTimestampTz().UtcTicks;
            long bucket = BinTicks(sourceTicks, originTicks, strideTicks);
            return new ValueTask<ValueRef>(
                ValueRef.FromTimestampTz(new DateTimeOffset(bucket, TimeSpan.Zero)));
        }
    }

    /// <summary>
    /// Snaps <paramref name="sourceTicks"/> down to the nearest multiple of
    /// <paramref name="strideTicks"/> measured from <paramref name="originTicks"/>.
    /// Uses floor-division so negative offsets round towards <c>-∞</c>, matching
    /// PG's <c>date_bin</c> semantics across the epoch boundary.
    /// </summary>
    internal static long BinTicks(long sourceTicks, long originTicks, long strideTicks)
    {
        long delta = sourceTicks - originTicks;
        long quotient = delta / strideTicks;
        long remainder = delta - quotient * strideTicks;
        if (remainder < 0)
        {
            quotient -= 1;
        }
        return originTicks + quotient * strideTicks;
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
