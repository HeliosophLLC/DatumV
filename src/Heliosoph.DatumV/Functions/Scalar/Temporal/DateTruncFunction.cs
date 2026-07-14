using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>date_trunc(field, source)</c> — rounds the source value
/// down to the beginning of the named field. Result kind matches the source
/// (<see cref="DataKind.Date"/>, <see cref="DataKind.Timestamp"/>,
/// <see cref="DataKind.TimestampTz"/>). The classic time-series bucketer
/// for fixed calendar boundaries.
/// </summary>
public sealed class DateTruncFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "date_trunc";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Truncates a temporal value to the start of the named field (year, month, day, hour, …).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Timestamp / TimestampTz preserve their kind; Date inputs collapse
        // to Timestamp (sub-day fields are no-ops but the time precision
        // promotion keeps callers honest if they mix sources).
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("field",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Timestamp)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("field",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.TimestampTz)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.TimestampTz)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("field",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Date)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Timestamp)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DateTruncFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(args[1].Kind switch
            {
                DataKind.Date => DataKind.Timestamp,
                DataKind.Timestamp => DataKind.Timestamp,
                DataKind.TimestampTz => DataKind.TimestampTz,
                _ => DataKind.Timestamp,
            }));
        }

        string field = args[0].AsString().ToLowerInvariant();
        return args[1].Kind switch
        {
            DataKind.Timestamp => new ValueTask<ValueRef>(
                ValueRef.FromTimestamp(TruncateDateTime(field, args[1].AsTimestamp()))),
            DataKind.TimestampTz => new ValueTask<ValueRef>(
                ValueRef.FromTimestampTz(TruncateDateTimeOffset(
                    field, args[1].AsTimestampTz(),
                    frame.Context?.SessionTimeZone ?? TimeZoneInfo.Utc))),
            DataKind.Date => new ValueTask<ValueRef>(
                ValueRef.FromTimestamp(TruncateDateTime(field, args[1].AsDate().ToDateTime(TimeOnly.MinValue)))),
            _ => throw new ExecutionException(
                $"date_trunc: unsupported source kind {args[1].Kind}."),
        };
    }

    /// <summary>
    /// Applies <c>date_trunc</c> semantics to a naive <see cref="DateTime"/>.
    /// Year truncates to Jan 1; month truncates to the 1st; week truncates to
    /// the Monday of the ISO week; quarter / decade / century / millennium
    /// all snap to the start of their respective windows.
    /// </summary>
    internal static DateTime TruncateDateTime(string field, DateTime dt) => field switch
    {
        "microsecond" => new DateTime(dt.Ticks - (dt.Ticks % 10), DateTimeKind.Unspecified),
        "millisecond" => new DateTime(dt.Ticks - (dt.Ticks % 10_000), DateTimeKind.Unspecified),
        "second" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, DateTimeKind.Unspecified),
        "minute" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Unspecified),
        "hour" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, DateTimeKind.Unspecified),
        "day" => new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Unspecified),
        "week" => TruncateToIsoWeekStart(dt),
        "month" => new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
        "quarter" => new DateTime(dt.Year, ((dt.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        "year" => new DateTime(dt.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        "decade" => new DateTime(dt.Year - dt.Year % 10, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        "century" => new DateTime(CenturyStart(dt.Year), 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        "millennium" => new DateTime(MillenniumStart(dt.Year), 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        _ => throw new ExecutionException($"date_trunc: unsupported field '{field}'."),
    };

    /// <summary>
    /// <see cref="DateTimeOffset"/> overload of <see cref="TruncateDateTime"/>.
    /// PG semantics: the wall clock is read in the session zone, truncated
    /// there, and the truncated wall clock is re-anchored in the same zone —
    /// so <c>date_trunc('day', …)</c> lands on the session zone's midnight,
    /// not UTC midnight. With the default UTC session the two coincide.
    /// </summary>
    internal static DateTimeOffset TruncateDateTimeOffset(string field, DateTimeOffset dto, TimeZoneInfo zone)
    {
        DateTime wallClock = TemporalSemantics.ToZoneWallClock(dto, zone).DateTime;
        DateTime trunc = TruncateDateTime(field, wallClock);
        return TemporalSemantics.InterpretInZone(trunc, zone);
    }

    private static DateTime TruncateToIsoWeekStart(DateTime dt)
    {
        // ISO weeks start on Monday. .NET's DayOfWeek treats Sunday as 0;
        // shift so Monday maps to 0 and Sunday maps to 6.
        int dow = ((int)dt.DayOfWeek + 6) % 7;
        DateTime midnight = new(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return midnight.AddDays(-dow);
    }

    private static int CenturyStart(int year) => (year - 1) / 100 * 100 + 1;
    private static int MillenniumStart(int year) => (year - 1) / 1000 * 1000 + 1;

    /// <inheritdoc />
    public bool IsPure => true;
}
