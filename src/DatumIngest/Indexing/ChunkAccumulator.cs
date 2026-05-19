using CardinalityEstimation;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Per-chunk column accumulator. Tracks min, max, null count, and a
/// cardinality estimator for a single column across the rows in one index chunk.
/// </summary>
/// <remarks>
/// <para>
/// Only retains <see cref="DataValue"/> instances that are self-contained: inline strings
/// (≤16 UTF-8 bytes, stored in the struct) and fixed-size scalars. On observing a non-inline
/// String, min/max tracking for that column is invalidated and no further
/// min/max is recorded — the serialized statistics will have <c>null</c> min/max, which
/// downstream zone-map pruning correctly treats as "no pruning possible."
/// </para>
/// <para>
/// Cardinality estimation is still updated for non-inline strings via <see cref="DataValue.RawContentHash"/>,
/// which is safe because the hash is either precomputed or derived from UTF-8 bytes at the
/// call site (before any source arena disposal).
/// </para>
/// </remarks>
internal sealed class ChunkAccumulator
{
    private DataValue? _minimum;
    private DataValue? _maximum;
    private long _nullCount;
    private bool _minMaxEligible = true;
    private readonly CardinalityEstimator? _cardinality;

    /// <summary>
    /// Creates a per-chunk accumulator.
    /// </summary>
    /// <param name="computeCardinality">
    /// When <c>true</c>, maintains a HyperLogLog sketch of distinct values and reports it
    /// via <see cref="ChunkColumnStatistics.EstimatedCardinality"/>. When <c>false</c>,
    /// the sketch is skipped entirely (reported cardinality is 0). HLL updates are ~100 ns
    /// per call; disabling saves meaningful wall time on high-cardinality columns when no
    /// downstream consumer uses the estimate.
    /// </param>
    public ChunkAccumulator(bool computeCardinality = true)
    {
        _cardinality = computeCardinality ? new CardinalityEstimator() : null;
    }

    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            _nullCount++;
            return;
        }

        UpdateMinMax(value);

        if (_cardinality is not null)
        {
            AddToCardinality(value);
        }
    }

    public ChunkColumnStatistics ToStatistics(long rowCount)
    {
        return new ChunkColumnStatistics(
            Minimum: _minimum,
            Maximum: _maximum,
            NullCount: _nullCount,
            RowCount: rowCount,
            EstimatedCardinality: _cardinality is null ? 0L : (long)_cardinality.Count());
    }

    private void UpdateMinMax(DataValue value)
    {
        if (!_minMaxEligible)
        {
            return;
        }

        if (!DataValueComparer.IsComparable(value.Kind))
        {
            return;
        }

        // Non-inline String can't be retained across batches without an external
        // arena. Rather than chase retention plumbing, we invalidate min/max for the column
        // on first sight. Downstream zone-map pruning treats null min/max as "unknown, do
        // not prune" — correct, conservative behaviour.
        if (value.Kind == DataKind.String && !value.IsInline)
        {
            _minimum = null;
            _maximum = null;
            _minMaxEligible = false;
            return;
        }

        if (_minimum is null || DataValueComparer.Compare(value, _minimum.Value) < 0)
        {
            _minimum = value;
        }

        if (_maximum is null || DataValueComparer.Compare(value, _maximum.Value) > 0)
        {
            _maximum = value;
        }
    }

    private void AddToCardinality(DataValue value)
    {
        switch (value.Kind)
        {
            case DataKind.Float32:
                _cardinality!.Add(value.AsFloat32());
                break;
            case DataKind.Float64:
                _cardinality!.Add(value.AsFloat64());
                break;
            case DataKind.UInt8:
                _cardinality!.Add((int)value.AsUInt8());
                break;
            case DataKind.Int8:
                _cardinality!.Add((int)value.AsInt8());
                break;
            case DataKind.Int16:
                _cardinality!.Add((int)value.AsInt16());
                break;
            case DataKind.UInt16:
                _cardinality!.Add((int)value.AsUInt16());
                break;
            case DataKind.Int32:
                _cardinality!.Add(value.AsInt32());
                break;
            case DataKind.UInt32:
                _cardinality!.Add((long)value.AsUInt32());
                break;
            case DataKind.Int64:
                _cardinality!.Add(value.AsInt64());
                break;
            case DataKind.UInt64:
                _cardinality!.Add((long)value.AsUInt64());
                break;
            case DataKind.Date:
                _cardinality!.Add(value.AsDate().DayNumber);
                break;
            case DataKind.TimestampTz:
                _cardinality!.Add(value.AsTimestampTz().UtcTicks);
                break;
            case DataKind.Timestamp:
                _cardinality!.Add(value.AsTimestamp().Ticks);
                break;
            case DataKind.String:
                _cardinality!.Add(value.RawContentHash);
                break;
            default:
                _cardinality!.Add(value.GetHashCode());
                break;
        }
    }
}
