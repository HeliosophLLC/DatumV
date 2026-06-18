using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// <c>date_add(part, amount, source)</c> — adds <c>amount</c> units of the
/// named <c>part</c> to a temporal source. DatumV extension in the T-SQL
/// <c>DATEADD</c> family; the PG-native idiom is <c>source + INTERVAL '...'</c>.
/// Calendar parts (year through day) preserve the input kind; sub-day parts
/// applied to a <see cref="DataKind.Date"/> promote the result to
/// <see cref="DataKind.Timestamp"/> so the smaller fields aren't silently lost.
/// </summary>
public sealed class DateAddFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "date_add";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Adds amount of the named part (year, month, day, hour, …) to a temporal value.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("amount", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Date)),
            ],
            VariadicTrailing: null,
            // Sub-day parts promote Date → Timestamp; calendar parts stay Date.
            // We register the widest kind here; the executor narrows when it can.
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("amount", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Timestamp)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("amount", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.TimestampTz)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.TimestampTz)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DateAddFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(ResultKind(args[2].Kind)));
        }

        string part = args[0].AsString().ToLowerInvariant();
        if (!args[1].TryToInt64(out long amount))
        {
            // Fractional amounts: scale through double for sub-day parts;
            // calendar parts require integer counts (PG semantics).
            if (!args[1].TryToDouble(out double dAmount))
            {
                throw new ExecutionException("date_add: amount must be a number.");
            }
            amount = (long)System.Math.Round(dAmount, MidpointRounding.AwayFromZero);
        }

        return args[2].Kind switch
        {
            DataKind.Date        => new ValueTask<ValueRef>(AddToDate(part, amount, args[2].AsDate())),
            DataKind.Timestamp   => new ValueTask<ValueRef>(ValueRef.FromTimestamp(AddToDateTime(part, amount, args[2].AsTimestamp()))),
            DataKind.TimestampTz => new ValueTask<ValueRef>(ValueRef.FromTimestampTz(AddToDateTimeOffset(part, amount, args[2].AsTimestampTz()))),
            _ => throw new ExecutionException($"date_add: unsupported source kind {args[2].Kind}."),
        };
    }

    private static DataKind ResultKind(DataKind sourceKind) => sourceKind switch
    {
        DataKind.TimestampTz => DataKind.TimestampTz,
        DataKind.Timestamp => DataKind.Timestamp,
        DataKind.Date => DataKind.Timestamp, // widest possible result when null
        _ => DataKind.Timestamp,
    };

    private static ValueRef AddToDate(string part, long amount, DateOnly d)
    {
        // Calendar parts preserve Date; sub-day parts promote to Timestamp.
        switch (NormalizePart(part))
        {
            case "year":       return ValueRef.FromDate(d.AddYears(checked((int)amount)));
            case "quarter":    return ValueRef.FromDate(d.AddMonths(checked((int)(amount * 3))));
            case "month":      return ValueRef.FromDate(d.AddMonths(checked((int)amount)));
            case "week":       return ValueRef.FromDate(d.AddDays(checked((int)(amount * 7))));
            case "day":        return ValueRef.FromDate(d.AddDays(checked((int)amount)));
            case "decade":     return ValueRef.FromDate(d.AddYears(checked((int)(amount * 10))));
            case "century":    return ValueRef.FromDate(d.AddYears(checked((int)(amount * 100))));
            case "millennium": return ValueRef.FromDate(d.AddYears(checked((int)(amount * 1000))));
            default:
                // Sub-day adds: promote to Timestamp at midnight.
                return ValueRef.FromTimestamp(AddToDateTime(part, amount, d.ToDateTime(TimeOnly.MinValue)));
        }
    }

    private static DateTime AddToDateTime(string part, long amount, DateTime dt) => NormalizePart(part) switch
    {
        "year"        => dt.AddYears(checked((int)amount)),
        "quarter"     => dt.AddMonths(checked((int)(amount * 3))),
        "month"       => dt.AddMonths(checked((int)amount)),
        "week"        => dt.AddDays(checked(amount * 7)),
        "day"         => dt.AddDays(amount),
        "hour"        => dt.AddHours(amount),
        "minute"      => dt.AddMinutes(amount),
        "second"      => dt.AddSeconds(amount),
        "millisecond" => dt.AddMilliseconds(amount),
        "microsecond" => dt.AddTicks(checked(amount * 10L)),
        "decade"      => dt.AddYears(checked((int)(amount * 10))),
        "century"     => dt.AddYears(checked((int)(amount * 100))),
        "millennium"  => dt.AddYears(checked((int)(amount * 1000))),
        _ => throw new ExecutionException($"date_add: unsupported part '{part}'."),
    };

    private static DateTimeOffset AddToDateTimeOffset(string part, long amount, DateTimeOffset dto) => NormalizePart(part) switch
    {
        "year"        => dto.AddYears(checked((int)amount)),
        "quarter"     => dto.AddMonths(checked((int)(amount * 3))),
        "month"       => dto.AddMonths(checked((int)amount)),
        "week"        => dto.AddDays(checked(amount * 7)),
        "day"         => dto.AddDays(amount),
        "hour"        => dto.AddHours(amount),
        "minute"      => dto.AddMinutes(amount),
        "second"      => dto.AddSeconds(amount),
        "millisecond" => dto.AddMilliseconds(amount),
        "microsecond" => dto.AddTicks(checked(amount * 10L)),
        "decade"      => dto.AddYears(checked((int)(amount * 10))),
        "century"     => dto.AddYears(checked((int)(amount * 100))),
        "millennium"  => dto.AddYears(checked((int)(amount * 1000))),
        _ => throw new ExecutionException($"date_add: unsupported part '{part}'."),
    };

    /// <summary>
    /// Folds the aliases documented in <c>docs/functions/temporal.md</c>
    /// (plurals + single-letter shortcuts) down to the canonical part name.
    /// </summary>
    internal static string NormalizePart(string part) => part switch
    {
        "years" or "y" => "year",
        "quarters" or "q" => "quarter",
        "months" or "m" => "month",
        "weeks" or "w" => "week",
        "days" or "d" => "day",
        "hours" or "h" => "hour",
        "minutes" or "min" => "minute",
        "seconds" or "s" => "second",
        "milliseconds" or "ms" => "millisecond",
        "microseconds" or "us" => "microsecond",
        "decades" => "decade",
        "centuries" => "century",
        "millennia" => "millennium",
        _ => part,
    };

    /// <inheritdoc />
    public bool IsPure => true;
}
