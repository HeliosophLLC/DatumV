using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// Implements <c>STDDEV(expression)</c> and <c>STDDEV_SAMP(expression)</c>
/// (sample standard deviation, N−1 denominator). Numerically stable via
/// Welford's online algorithm. Returns null when fewer than two non-null
/// values are observed.
/// </summary>
public sealed class StandardDeviationFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "STDDEV";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Sample standard deviation (N−1 denominator) of non-null numeric values; numerically stable via Welford's algorithm.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException($"{Name}() requires exactly one argument.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException($"{Name}() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new WelfordStandardDeviationAccumulator(usePopulation: false);
}

/// <summary>
/// Implements <c>STDDEV_POP(expression)</c> (population standard deviation,
/// N denominator). Numerically stable via Welford's online algorithm.
/// </summary>
public sealed class StandardDeviationPopulationFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "STDDEV_POP";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Population standard deviation (N denominator) of non-null numeric values; numerically stable via Welford's algorithm.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException($"{Name}() requires exactly one argument.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException($"{Name}() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new WelfordStandardDeviationAccumulator(usePopulation: true);
}

/// <summary>
/// Welford's online algorithm accumulator that returns the square root of
/// variance as the final result.
/// </summary>
internal sealed class WelfordStandardDeviationAccumulator : IAggregateAccumulator
{
    private readonly bool _usePopulation;
    private long _count;
    private double _mean;
    private double _m2;

    public WelfordStandardDeviationAccumulator(bool usePopulation)
    {
        _usePopulation = usePopulation;
    }

    public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        if (arguments[0].IsNull) return;

        double value = arguments[0].ToDouble();
        _count++;
        double delta = value - _mean;
        _mean += delta / _count;
        double delta2 = value - _mean;
        _m2 += delta * delta2;
    }

    /// <inheritdoc/>
    public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
    {
        WelfordStandardDeviationAccumulator otherAccumulator = (WelfordStandardDeviationAccumulator)other;

        if (otherAccumulator._count == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_count == 0)
        {
            _count = otherAccumulator._count;
            _mean = otherAccumulator._mean;
            _m2 = otherAccumulator._m2;
            return ValueTask.CompletedTask;
        }

        long combinedCount = _count + otherAccumulator._count;
        double delta = otherAccumulator._mean - _mean;
        _m2 += otherAccumulator._m2 + delta * delta * _count * otherAccumulator._count / combinedCount;
        _mean += delta * otherAccumulator._count / combinedCount;
        _count = combinedCount;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
    {
        if (_usePopulation)
        {
            return new(_count > 0
                ? DataValue.FromFloat64(System.Math.Sqrt(_m2 / _count))
                : DataValue.Null(DataKind.Float64));
        }

        return new(_count > 1
            ? DataValue.FromFloat64(System.Math.Sqrt(_m2 / (_count - 1)))
            : DataValue.Null(DataKind.Float64));
    }

    /// <inheritdoc />
    public void Reset()
    {
        _count = 0;
        _mean = 0;
        _m2 = 0;
    }
}
