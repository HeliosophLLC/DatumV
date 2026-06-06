using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Temporal;

/// <summary>
/// PG <c>justify_hours(interval)</c> — pushes microseconds spanning multiples
/// of 24 hours into the day component. Days and months are left alone.
/// </summary>
public sealed class JustifyHoursFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "justify_hours";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Pushes excess hours in an interval into the day component (24h boundary).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Interval))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JustifyHoursFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef src = arguments.Span[0];
        return src.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval))
            : new ValueTask<ValueRef>(ValueRef.FromInterval(src.AsInterval().JustifyHours()));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// PG <c>justify_days(interval)</c> — pushes days spanning multiples of 30
/// into the month component. Microseconds are left alone.
/// </summary>
public sealed class JustifyDaysFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "justify_days";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Pushes excess days in an interval into the month component (30-day boundary).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Interval))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JustifyDaysFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef src = arguments.Span[0];
        return src.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval))
            : new ValueTask<ValueRef>(ValueRef.FromInterval(src.AsInterval().JustifyDays()));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}

/// <summary>
/// PG <c>justify_interval(interval)</c> — applies <c>justify_hours</c> then
/// <c>justify_days</c>, then aligns each component's sign with the interval's
/// net direction.
/// </summary>
public sealed class JustifyIntervalFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "justify_interval";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Temporal;

    /// <inheritdoc />
    public static string Description =>
        "Fully normalises an interval (justify_hours then justify_days plus sign alignment).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("source", DataKindMatcher.Exact(DataKind.Interval))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Interval)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JustifyIntervalFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef src = arguments.Span[0];
        return src.IsNull
            ? new ValueTask<ValueRef>(ValueRef.Null(DataKind.Interval))
            : new ValueTask<ValueRef>(ValueRef.FromInterval(src.AsInterval().JustifyInterval()));
    }

    /// <inheritdoc />
    public bool IsPure => true;
}
