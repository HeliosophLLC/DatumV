namespace Heliosoph.DatumV.Statistics.Accumulators;

using System.IO.Hashing;
using Heliosoph.DatumV.Model;
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
    public void Add(DataValue value, IValueStore store)
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
            case DataKind.TimestampTz:
                _estimator.Add(value.AsTimestampTz().UtcTicks);
                break;
            case DataKind.Timestamp:
                _estimator.Add(value.AsTimestamp().Ticks);
                break;
            case DataKind.String:
                // Feed HLL the cached 64-bit XxHash of the UTF-8 bytes instead of
                // materializing a managed string. HLL is hash-agnostic — distinct
                // hashes still produce distinct bucket positions. Arena-slice values
                // without a cached hash (RawContentHash == 0) fall back to hashing
                // the UTF-8 bytes on the fly, zero-allocation.
                {
                    ulong stringHash = value.RawContentHash;
                    if (stringHash == 0)
                        stringHash = XxHash64.HashToUInt64(value.AsUtf8Span(store));
                    _estimator.Add(unchecked((long)stringHash));
                }
                break;
            case DataKind.Uuid:
                // Guid.GetHashCode combines all 128 bits into a 32-bit int — good enough
                // for HLL bucketing. No string materialisation.
                _estimator.Add(value.AsUuid().GetHashCode());
                break;
            default:
                _estimator.Add(value.GetHashCode());
                break;
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        yield return new StatisticResult("cardinality", new CardinalityResult(EstimatedCardinality));
    }
}

/// <summary>
/// Contains the cardinality estimation result.
/// </summary>
/// <param name="EstimatedDistinctCount">Approximate number of distinct values.</param>
public sealed record CardinalityResult(long EstimatedDistinctCount);
