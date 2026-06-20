using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>Shared signature/plumbing for the four Duration → Float32 readouts.</summary>
internal static class DurationExtractionHelpers
{
    /// <summary>Single signature: Duration in, Float32 out.</summary>
    public static IReadOnlyList<FunctionSignatureVariant> DurationToFloat32 { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("dur", DataKindMatcher.Exact(DataKind.Duration))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];
}

/// <summary>Total seconds in a Duration as Float32 (fractional).</summary>
public sealed class DurationSecondsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "duration_seconds";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Total seconds in a Duration as Float32 (fractional).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => DurationExtractionHelpers.DurationToFloat32;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DurationSecondsFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32((float)v.AsDuration().TotalSeconds));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Total minutes in a Duration as Float32 (fractional).</summary>
public sealed class DurationMinutesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "duration_minutes";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Total minutes in a Duration as Float32 (fractional).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => DurationExtractionHelpers.DurationToFloat32;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DurationMinutesFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32((float)v.AsDuration().TotalMinutes));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Total hours in a Duration as Float32 (fractional).</summary>
public sealed class DurationHoursFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "duration_hours";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Total hours in a Duration as Float32 (fractional).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => DurationExtractionHelpers.DurationToFloat32;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DurationHoursFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32((float)v.AsDuration().TotalHours));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>Total days in a Duration as Float32 (fractional).</summary>
public sealed class DurationDaysFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "duration_days";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;
    /// <inheritdoc />
    public static string Description => "Total days in a Duration as Float32 (fractional).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => DurationExtractionHelpers.DurationToFloat32;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<DurationDaysFunction>(argumentKinds);
    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef v = arguments.Span[0];
        return v.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32))
            : new ValueTask<ValueRef>(ValueRef.FromFloat32((float)v.AsDuration().TotalDays));
    }
    /// <inheritdoc />
    public bool IsPure => true;
}
