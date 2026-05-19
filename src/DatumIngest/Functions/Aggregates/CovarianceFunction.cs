using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// Implements <c>COVAR_POP(y, x)</c> (population covariance, N denominator).
/// Numerically stable via an online co-moment algorithm parallel to
/// Welford's variance algorithm.
/// </summary>
public sealed class CovariancePopulationFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "COVAR_POP";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Population covariance (N denominator) between two numeric columns; online co-moment algorithm.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: y and x.");
        }
        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"{Name}() first argument (y) must be numeric, got {argumentKinds[0]}.");
        }
        if (!DataValueComparer.IsNumericScalar(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"{Name}() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }
        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CovarianceAccumulator(usePopulation: true);
}

/// <summary>
/// Implements <c>COVAR_SAMP(y, x)</c> (sample covariance, N−1 denominator).
/// Numerically stable via an online co-moment algorithm.
/// </summary>
public sealed class CovarianceSampleFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "COVAR_SAMP";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Sample covariance (N−1 denominator) between two numeric columns; online co-moment algorithm.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("y", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("x", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: y and x.");
        }
        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"{Name}() first argument (y) must be numeric, got {argumentKinds[0]}.");
        }
        if (!DataValueComparer.IsNumericScalar(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"{Name}() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }
        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CovarianceAccumulator(usePopulation: false);
}

/// <summary>
/// Online co-moment accumulator. Tracks running means for both variables
/// and the cross-moment. Pairs where either value is null are skipped.
/// </summary>
internal sealed class CovarianceAccumulator : IAggregateAccumulator
{
    private readonly bool _usePopulation;
    private long _count;
    private double _meanY;
    private double _meanX;
    private double _coMoment;

    public CovarianceAccumulator(bool usePopulation)
    {
        _usePopulation = usePopulation;
    }

    public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return;

        double y = arguments[0].ToDouble();
        double x = arguments[1].ToDouble();

        _count++;

        double deltaY = y - _meanY;
        _meanY += deltaY / _count;

        double deltaX = x - _meanX;
        _meanX += deltaX / _count;

        _coMoment += deltaY * (x - _meanX);
    }

    /// <inheritdoc/>
    public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
    {
        CovarianceAccumulator otherAccumulator = (CovarianceAccumulator)other;

        if (otherAccumulator._count == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_count == 0)
        {
            _count = otherAccumulator._count;
            _meanY = otherAccumulator._meanY;
            _meanX = otherAccumulator._meanX;
            _coMoment = otherAccumulator._coMoment;
            return ValueTask.CompletedTask;
        }

        long combinedCount = _count + otherAccumulator._count;
        double deltaY = otherAccumulator._meanY - _meanY;
        double deltaX = otherAccumulator._meanX - _meanX;
        _coMoment += otherAccumulator._coMoment + deltaY * deltaX * _count * otherAccumulator._count / combinedCount;
        _meanY += deltaY * otherAccumulator._count / combinedCount;
        _meanX += deltaX * otherAccumulator._count / combinedCount;
        _count = combinedCount;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
    {
        if (_usePopulation)
        {
            return new(_count > 0
                ? DataValue.FromFloat64(_coMoment / _count)
                : DataValue.Null(DataKind.Float64));
        }

        return new(_count > 1
            ? DataValue.FromFloat64(_coMoment / (_count - 1))
            : DataValue.Null(DataKind.Float64));
    }

    /// <inheritdoc />
    public void Reset()
    {
        _count = 0;
        _meanY = 0;
        _meanX = 0;
        _coMoment = 0;
    }
}
