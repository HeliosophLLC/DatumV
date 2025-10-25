using CardinalityEstimation;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Per-chunk column accumulator used by <see cref="IncrementalIndexBuilder"/>. Tracks
/// min, max, null count, and a cardinality estimator for a single column across the
/// rows in one index chunk.
/// </summary>
/// <remarks>
/// Near-identical in behaviour to <c>SourceIndexBuilder.ChunkAccumulator</c> (the inner
/// class used by the streaming <c>BuildAsync</c> path); kept separate because the
/// incremental builder is the production path and may evolve independently.
/// </remarks>
internal sealed class IncrementalChunkAccumulator
{
    private DataValue? _minimum;
    private DataValue? _maximum;
    private long _nullCount;
    private readonly CardinalityEstimator _cardinality = new();

    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            _nullCount++;
            return;
        }

        UpdateMinMax(value);
        AddToCardinality(value);
    }

    public ChunkColumnStatistics ToStatistics(long rowCount)
    {
        return new ChunkColumnStatistics(
            Minimum: _minimum,
            Maximum: _maximum,
            NullCount: _nullCount,
            RowCount: rowCount,
            EstimatedCardinality: (long)_cardinality.Count());
    }

    private void UpdateMinMax(DataValue value)
    {
        if (!IsComparableKind(value.Kind))
        {
            return;
        }

        if (_minimum is null || StatisticsPredicateEvaluator.CompareValues(value, _minimum.Value) < 0)
        {
            _minimum = value;
        }

        if (_maximum is null || StatisticsPredicateEvaluator.CompareValues(value, _maximum.Value) > 0)
        {
            _maximum = value;
        }
    }

    private void AddToCardinality(DataValue value)
    {
        switch (value.Kind)
        {
            case DataKind.Float32:
                _cardinality.Add(value.AsFloat32());
                break;
            case DataKind.Float64:
                _cardinality.Add(value.AsFloat64());
                break;
            case DataKind.UInt8:
                _cardinality.Add((int)value.AsUInt8());
                break;
            case DataKind.Int8:
                _cardinality.Add((int)value.AsInt8());
                break;
            case DataKind.Int16:
                _cardinality.Add((int)value.AsInt16());
                break;
            case DataKind.UInt16:
                _cardinality.Add((int)value.AsUInt16());
                break;
            case DataKind.Int32:
                _cardinality.Add(value.AsInt32());
                break;
            case DataKind.UInt32:
                _cardinality.Add((long)value.AsUInt32());
                break;
            case DataKind.Int64:
                _cardinality.Add(value.AsInt64());
                break;
            case DataKind.UInt64:
                _cardinality.Add((long)value.AsUInt64());
                break;
            case DataKind.String:
                _cardinality.Add(value.RawContentHash);
                break;
            case DataKind.Date:
                _cardinality.Add(value.AsDate().DayNumber);
                break;
            case DataKind.DateTime:
                _cardinality.Add(value.AsDateTime().ToUnixTimeMilliseconds());
                break;
            case DataKind.JsonValue:
                _cardinality.Add(value.RawContentHash);
                break;
            default:
                _cardinality.Add(value.GetHashCode());
                break;
        }
    }

    private static bool IsComparableKind(DataKind kind)
    {
        return kind is DataKind.Float32 or DataKind.Float64
            or DataKind.UInt8 or DataKind.Int8
            or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32
            or DataKind.Int64 or DataKind.UInt64
            or DataKind.String or DataKind.Date or DataKind.DateTime;
    }
}
