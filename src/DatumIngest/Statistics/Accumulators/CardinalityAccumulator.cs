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

        // Use numeric overloads to avoid per-value string allocation.
        // The CardinalityEstimation library hashes numeric primitives directly.
        switch (value.Kind)
        {
            case DataKind.Float32:
                _estimator.Add(value.AsFloat32());
                break;
            case DataKind.Float64:
                _estimator.Add(value.AsFloat64());
                break;
            case DataKind.UInt8:
                _estimator.Add((int)value.AsUInt8());
                break;
            case DataKind.Int8:
                _estimator.Add((int)value.AsInt8());
                break;
            case DataKind.Int16:
                _estimator.Add((int)value.AsInt16());
                break;
            case DataKind.UInt16:
                _estimator.Add((int)value.AsUInt16());
                break;
            case DataKind.Int32:
                _estimator.Add(value.AsInt32());
                break;
            case DataKind.UInt32:
                _estimator.Add((long)value.AsUInt32());
                break;
            case DataKind.Int64:
                _estimator.Add(value.AsInt64());
                break;
            case DataKind.UInt64:
                _estimator.Add((long)value.AsUInt64());
                break;
            case DataKind.Boolean:
                _estimator.Add(value.AsBoolean() ? 1 : 0);
                break;
            case DataKind.Date:
                _estimator.Add(value.AsDate().DayNumber);
                break;
            case DataKind.DateTime:
                _estimator.Add(value.AsDateTime().ToUnixTimeMilliseconds());
                break;
            case DataKind.String:
                _estimator.Add(value.AsString());
                break;
            case DataKind.Uuid:
                _estimator.Add(value.AsUuid().ToString());
                break;
            case DataKind.JsonValue:
                _estimator.Add(value.AsJsonValue());
                break;
            default:
                _estimator.Add(value.GetHashCode());
                break;
        }
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
