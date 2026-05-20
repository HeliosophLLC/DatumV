using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// Implements <c>CORR(y, x)</c>. Computes the Pearson correlation coefficient
/// between two numeric columns using an online co-moment algorithm (parallel
/// to Welford's algorithm for variance).
/// <para>
/// Returns a value in [−1, 1], or null if fewer than 2 non-null pairs are
/// available or if either variable has zero variance.
/// </para>
/// <para>
/// Memory: O(1) per group — only running statistics are maintained.
/// </para>
/// </summary>
public sealed class CorrelationFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "CORR";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Pearson correlation coefficient between two numeric columns (online co-moment).";

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
            throw new ArgumentException("CORR() requires exactly two arguments: y and x.");
        }

        bool firstIsNumeric = argumentKinds[0] is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64;
        if (!firstIsNumeric)
        {
            throw new ArgumentException(
                $"CORR() first argument (y) must be numeric, got {argumentKinds[0]}.");
        }

        bool secondIsNumeric = argumentKinds[1] is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64;
        if (!secondIsNumeric)
        {
            throw new ArgumentException(
                $"CORR() second argument (x) must be numeric, got {argumentKinds[1]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CorrelationAccumulator();

    /// <summary>
    /// Online co-moment accumulator that tracks running means, M2 for each
    /// variable, and the cross-moment (co-moment). Pairs where either value
    /// is null are skipped entirely.
    /// </summary>
    private sealed class CorrelationAccumulator : IAggregateAccumulator
    {
        private long _count;
        private double _meanY;
        private double _meanX;
        private double _m2Y;
        private double _m2X;
        private double _coMoment;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull || arguments[1].IsNull) return;

            double y = arguments[0].ToDouble();
            double x = arguments[1].ToDouble();

            _count++;

            double deltaY = y - _meanY;
            _meanY += deltaY / _count;
            double deltaY2 = y - _meanY;
            _m2Y += deltaY * deltaY2;

            double deltaX = x - _meanX;
            _meanX += deltaX / _count;
            double deltaX2 = x - _meanX;
            _m2X += deltaX * deltaX2;

            // Co-moment uses the new mean of X with the old delta of Y.
            _coMoment += deltaY * (x - _meanX);
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            CorrelationAccumulator otherAccumulator = (CorrelationAccumulator)other;

            if (otherAccumulator._count == 0)
            {
                return ValueTask.CompletedTask;
            }

            if (_count == 0)
            {
                _count = otherAccumulator._count;
                _meanY = otherAccumulator._meanY;
                _meanX = otherAccumulator._meanX;
                _m2Y = otherAccumulator._m2Y;
                _m2X = otherAccumulator._m2X;
                _coMoment = otherAccumulator._coMoment;
                return ValueTask.CompletedTask;
            }

            long combinedCount = _count + otherAccumulator._count;
            double deltaY = otherAccumulator._meanY - _meanY;
            double deltaX = otherAccumulator._meanX - _meanX;
            _m2Y += otherAccumulator._m2Y + deltaY * deltaY * _count * otherAccumulator._count / combinedCount;
            _m2X += otherAccumulator._m2X + deltaX * deltaX * _count * otherAccumulator._count / combinedCount;
            _coMoment += otherAccumulator._coMoment + deltaY * deltaX * _count * otherAccumulator._count / combinedCount;
            _meanY += deltaY * otherAccumulator._count / combinedCount;
            _meanX += deltaX * otherAccumulator._count / combinedCount;
            _count = combinedCount;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_count < 2)
            {
                return new(DataValue.Null(DataKind.Float64));
            }

            double denominator = System.Math.Sqrt(_m2Y * _m2X);

            if (denominator == 0.0)
            {
                return new(DataValue.Null(DataKind.Float64));
            }

            return new(DataValue.FromFloat64(_coMoment / denominator));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _count = 0;
            _meanY = 0;
            _meanX = 0;
            _m2Y = 0;
            _m2X = 0;
            _coMoment = 0;
        }
    }
}
