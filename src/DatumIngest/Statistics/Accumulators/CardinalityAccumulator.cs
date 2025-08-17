namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;
using CardinalityEstimation;

/// <summary>
/// Estimates distinct value count using HyperLogLog via the CardinalityEstimation library.
/// Provides approximate cardinality with low memory overhead.
/// </summary>
public sealed class CardinalityAccumulator : IStatisticAccumulator
{
    private CardinalityEstimator _estimator = new();

    /// <summary>Gets the estimated distinct count.</summary>
    public long EstimatedCardinality => (long)_estimator.Count();

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        // Scalar uses the raw float bit pattern as an integer string — faster than
        // float.ToString("R") and still unique per distinct float value.
        string representation = value.Kind switch
        {
            DataKind.Float32 => BitConverter.SingleToInt32Bits(value.AsFloat32()).ToString(),
            DataKind.Float64 => BitConverter.DoubleToInt64Bits(value.AsFloat64()).ToString(),
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.Int8 => value.AsInt8().ToString(),
            DataKind.Int16 => value.AsInt16().ToString(),
            DataKind.UInt16 => value.AsUInt16().ToString(),
            DataKind.Int32 => value.AsInt32().ToString(),
            DataKind.UInt32 => value.AsUInt32().ToString(),
            DataKind.Int64 => value.AsInt64().ToString(),
            DataKind.UInt64 => value.AsUInt64().ToString(),
            DataKind.String => value.AsString(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.JsonValue => value.AsJsonValue(),
            _ => value.GetHashCode().ToString()
        };

        _estimator.Add(representation);
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is CardinalityAccumulator otherCardinality)
        {
            _estimator.Merge(otherCardinality._estimator);
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        return new StatisticResult("cardinality", new CardinalityResult(EstimatedCardinality));
    }
}

/// <summary>
/// Contains the cardinality estimation result.
/// </summary>
/// <param name="EstimatedDistinctCount">Approximate number of distinct values.</param>
public sealed record CardinalityResult(long EstimatedDistinctCount);
