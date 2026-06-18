using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// <c>date_diff(part, start, end)</c> — counts the number of part boundaries
/// crossed between two temporal values, returning a <see cref="DataKind.Float32"/>.
/// DatumV extension in the T-SQL <c>DATEDIFF</c> family; the PG-native idiom
/// is <c>EXTRACT(EPOCH FROM (end - start)) / &lt;unit-in-seconds&gt;</c> or
/// <c>age()</c>.
/// </summary>
/// <remarks>
/// Calendar parts (year/quarter/month/week/day) count boundary crossings —
/// e.g. <c>date_diff('day', '2026-06-11 23:00', '2026-06-12 01:00') = 1</c>,
/// not 0.083. Sub-day parts return the elapsed delta in that unit (which can
/// be fractional via Float32). The result is positive when <c>end &gt; start</c>.
/// </remarks>
public sealed class DateDiffFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "date_diff";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Counts part boundaries between two temporal values (T-SQL DATEDIFF-style).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Exact(DataKind.Date)),
                new ParameterSpec("end",   DataKindMatcher.Exact(DataKind.Date)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Exact(DataKind.Timestamp)),
                new ParameterSpec("end",   DataKindMatcher.Exact(DataKind.Timestamp)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("part",  DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("start", DataKindMatcher.Exact(DataKind.TimestampTz)),
                new ParameterSpec("end",   DataKindMatcher.Exact(DataKind.TimestampTz)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DateDiffFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        string part = DateAddFunction.NormalizePart(args[0].AsString().ToLowerInvariant());
        DateTime start;
        DateTime end;
        switch (args[1].Kind)
        {
            case DataKind.Date:
                start = args[1].AsDate().ToDateTime(TimeOnly.MinValue);
                end   = args[2].AsDate().ToDateTime(TimeOnly.MinValue);
                break;
            case DataKind.Timestamp:
                start = args[1].AsTimestamp();
                end   = args[2].AsTimestamp();
                break;
            case DataKind.TimestampTz:
                start = args[1].AsTimestampTz().UtcDateTime;
                end   = args[2].AsTimestampTz().UtcDateTime;
                break;
            default:
                throw new ExecutionException($"date_diff: unsupported source kind {args[1].Kind}.");
        }

        float result = ComputeDiff(part, start, end);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(result));
    }

    private static float ComputeDiff(string part, DateTime start, DateTime end) => part switch
    {
        // Calendar parts: boundary crossings.
        "year"       => end.Year - start.Year,
        "quarter"    => (end.Year - start.Year) * 4 + ((end.Month - 1) / 3 - (start.Month - 1) / 3),
        "month"      => (end.Year - start.Year) * 12 + (end.Month - start.Month),
        "week"       => (float)System.Math.Floor((end.Date - start.Date).TotalDays / 7.0),
        "day"        => (float)(end.Date - start.Date).TotalDays,
        "decade"     => end.Year / 10 - start.Year / 10,
        "century"    => (end.Year - 1) / 100 - (start.Year - 1) / 100,
        "millennium" => (end.Year - 1) / 1000 - (start.Year - 1) / 1000,
        // Sub-day parts: elapsed delta (may be fractional).
        "hour"        => (float)(end - start).TotalHours,
        "minute"      => (float)(end - start).TotalMinutes,
        "second"      => (float)(end - start).TotalSeconds,
        "millisecond" => (float)(end - start).TotalMilliseconds,
        "microsecond" => (end.Ticks - start.Ticks) / 10f,
        _ => throw new ExecutionException($"date_diff: unsupported part '{part}'."),
    };

    /// <inheritdoc />
    public bool IsPure => true;
}
