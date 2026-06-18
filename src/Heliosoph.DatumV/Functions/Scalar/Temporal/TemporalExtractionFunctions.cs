using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// Shared plumbing for the single-component date/time extractor shorthands
/// (<c>year</c>, <c>month</c>, <c>day</c>, <c>hour</c>, <c>minute</c>,
/// <c>second</c>, <c>quarter</c>, <c>dayofweek</c>, <c>dayofyear</c>). These
/// are DatumV-style conveniences that wrap <c>date_part</c>; they all return
/// <see cref="DataKind.Float32"/>.
/// </summary>
internal static class TemporalExtractionHelpers
{
    /// <summary>Signatures for extractors that accept Date / Timestamp / TimestampTz.</summary>
    public static IReadOnlyList<FunctionSignatureVariant> DateLikeSignatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Date))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Timestamp))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.TimestampTz))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <summary>Signatures for extractors that additionally accept Time.</summary>
    public static IReadOnlyList<FunctionSignatureVariant> DateOrTimeSignatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Date))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Timestamp))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.TimestampTz))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Time))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <summary>
    /// Reads a calendar-date view of any of the supported temporal kinds. Time
    /// inputs collapse to the unix-epoch date (year 1 in the .NET model) — the
    /// caller is expected to reject Time before reaching here for calendar
    /// fields.
    /// </summary>
    public static DateOnly ToDate(ValueRef v) => v.Kind switch
    {
        DataKind.Date => v.AsDate(),
        DataKind.Timestamp => DateOnly.FromDateTime(v.AsTimestamp()),
        DataKind.TimestampTz => DateOnly.FromDateTime(v.AsTimestampTz().UtcDateTime),
        _ => throw new ExecutionException(
            $"Temporal extractor: cannot derive a calendar date from kind {v.Kind}."),
    };

    /// <summary>
    /// Reads a clock-time view. Date inputs collapse to midnight; TimestampTz
    /// inputs route through the UTC face for offset-agnostic sub-day fields.
    /// </summary>
    public static TimeOnly ToTime(ValueRef v) => v.Kind switch
    {
        DataKind.Date => TimeOnly.MinValue,
        DataKind.Timestamp => TimeOnly.FromDateTime(v.AsTimestamp()),
        DataKind.TimestampTz => TimeOnly.FromDateTime(v.AsTimestampTz().UtcDateTime),
        DataKind.Time => v.AsTime(),
        _ => throw new ExecutionException(
            $"Temporal extractor: cannot derive a clock time from kind {v.Kind}."),
    };
}

/// <summary>Extract calendar year as Float32.</summary>
public sealed class YearFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "year";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the calendar year from a Date, Timestamp, or TimestampTz.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<YearFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToDate(v).Year));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract calendar month (1-12) as Float32.</summary>
public sealed class MonthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "month";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the month (1-12) from a Date, Timestamp, or TimestampTz.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MonthFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToDate(v).Month));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract day of month (1-31) as Float32.</summary>
public sealed class DayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "day";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the day of month (1-31) from a Date, Timestamp, or TimestampTz.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DayFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToDate(v).Day));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract hour (0-23) as Float32. Date inputs yield 0.</summary>
public sealed class HourFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "hour";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the hour (0-23) from a temporal value. Date inputs yield 0.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateOrTimeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HourFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToTime(v).Hour));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract minute (0-59) as Float32. Date inputs yield 0.</summary>
public sealed class MinuteFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "minute";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the minute (0-59) from a temporal value. Date inputs yield 0.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateOrTimeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MinuteFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToTime(v).Minute));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// Extract whole second (0-59) as Float32. Date inputs yield 0. Unlike
/// <c>date_part('second', ...)</c> which returns fractional seconds, this
/// shorthand returns only the whole-second component.
/// </summary>
public sealed class SecondFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "second";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the whole second (0-59) from a temporal value. Date inputs yield 0.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateOrTimeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SecondFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToTime(v).Second));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract quarter (1-4) as Float32.</summary>
public sealed class QuarterFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "quarter";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Extracts the quarter (1-4) from a Date, Timestamp, or TimestampTz.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<QuarterFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        if (v.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        DateOnly d = TemporalExtractionHelpers.ToDate(v);
        return new ValueTask<ValueRef>(ValueRef.FromFloat32((d.Month + 2) / 3));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// ISO 8601 day of week: 1 (Monday) through 7 (Sunday), as Float32. Distinct
/// from <c>date_part('dow', ...)</c>, which uses the PG convention
/// (0 = Sunday).
/// </summary>
public sealed class DayOfWeekFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "dayofweek";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "ISO 8601 day of week: 1 (Monday) through 7 (Sunday).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DayOfWeekFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        if (v.IsNull) return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        DateOnly d = TemporalExtractionHelpers.ToDate(v);
        // .NET DayOfWeek: Sunday=0..Saturday=6. Shift to ISO: Monday=1..Sunday=7.
        int iso = ((int)d.DayOfWeek + 6) % 7 + 1;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(iso));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Extract day of year (1-366) as Float32.</summary>
public sealed class DayOfYearFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "dayofyear";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Day of year (1-366) from a Date, Timestamp, or TimestampTz.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => TemporalExtractionHelpers.DateLikeSignatures;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DayOfYearFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32(TemporalExtractionHelpers.ToDate(v).DayOfYear));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}
