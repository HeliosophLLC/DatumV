using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Estimates the memory footprint of rows held in hash-based structures (dictionaries,
/// hash sets) using a sampling strategy.
/// <para>
/// For fixed-width schemas (all columns are Scalar, UInt8, Boolean, Date, DateTime, Time,
/// Duration, or Uuid), the per-row size is computed exactly once on the first row.
/// For variable-width schemas (any column is String, Vector, Matrix, Tensor, Image,
/// UInt8Array, or JsonValue), every Nth row is sampled to maintain a running average.
/// When the estimate crosses a budget threshold, callers should escalate to every-row
/// sampling for higher accuracy near the spill decision point.
/// </para>
/// </summary>
internal sealed class MemoryEstimator
{
    /// <summary>Per-DataValue object overhead estimate in bytes (reference, kind, isNull, payload pointer, shape pointer).</summary>
    internal const long DataValueOverheadBytes = 40;

    /// <summary>Per-dictionary/hash-set entry overhead estimate in bytes (hash, key ref, value ref, next).</summary>
    internal const long DictionaryEntryOverheadBytes = 48;

    /// <summary>Default number of rows between memory samples for variable-width schemas.</summary>
    private const int DefaultSampleInterval = 64;

    /// <summary>Budget threshold fraction at which sampling should switch to every row.</summary>
    internal const double EscalationThreshold = 0.75;

    private long _totalRowCount;
    private long _sampleCount;
    private long _totalSampledBytes;
    private bool _isFixedWidth;
    private bool _fixedWidthDetermined;
    private long _fixedRowSize;
    private int _sampleInterval = DefaultSampleInterval;

    /// <summary>Whether the next row should be sampled for size estimation.</summary>
    internal bool ShouldSample()
    {
        if (_fixedWidthDetermined && _isFixedWidth)
        {
            return false;
        }

        return _totalRowCount % _sampleInterval == 0;
    }

    /// <summary>Records a sampled row's estimated size.</summary>
    internal void RecordSample(Row row)
    {
        long rowBytes = EstimateRowBytes(row);

        if (!_fixedWidthDetermined)
        {
            _isFixedWidth = IsFixedWidthRow(row);
            _fixedWidthDetermined = true;

            if (_isFixedWidth)
            {
                _fixedRowSize = rowBytes;
                return;
            }
        }

        _sampleCount++;
        _totalSampledBytes += rowBytes;
    }

    /// <summary>Increments the total row count (called for every row, not just samples).</summary>
    internal void IncrementRowCount()
    {
        _totalRowCount++;
    }

    /// <summary>Switches to sampling every row (called when nearing budget threshold).</summary>
    internal void EscalateToEveryRow()
    {
        _sampleInterval = 1;
    }

    /// <summary>Estimates total memory consumed by all rows seen so far.</summary>
    internal long EstimateTotalBytes()
    {
        return EstimateBytesForRowCount(_totalRowCount);
    }

    /// <summary>
    /// Estimates memory consumed by a given number of rows, using the same per-row
    /// average derived from sampling. This allows callers to estimate memory for a
    /// subset of rows (e.g. only those held in memory, excluding spilled rows).
    /// </summary>
    /// <param name="rowCount">The number of rows to estimate memory for.</param>
    internal long EstimateBytesForRowCount(long rowCount)
    {
        if (rowCount == 0)
        {
            return 0;
        }

        if (_fixedWidthDetermined && _isFixedWidth)
        {
            return rowCount * _fixedRowSize;
        }

        if (_sampleCount == 0)
        {
            return 0;
        }

        long averageRowBytes = _totalSampledBytes / _sampleCount;
        return rowCount * averageRowBytes;
    }

    private static bool IsFixedWidthRow(Row row)
    {
        for (int index = 0; index < row.FieldCount; index++)
        {
            DataKind kind = row[index].Kind;
            if (kind is DataKind.String or DataKind.Vector or DataKind.Matrix
                or DataKind.Tensor or DataKind.Image or DataKind.UInt8Array
                or DataKind.JsonValue or DataKind.Array)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Estimates the memory consumed by a single row in a hash-based collection.</summary>
    internal static long EstimateRowBytes(Row row)
    {
        long bytes = 0;

        for (int index = 0; index < row.FieldCount; index++)
        {
            DataValue value = row[index];
            bytes += DataValueOverheadBytes;

            if (value.IsNull)
            {
                continue;
            }

            bytes += value.Kind switch
            {
                DataKind.Float32 => 4,
                DataKind.UInt8 => 1,
                DataKind.Boolean => 1,
                DataKind.Date => 4,
                DataKind.DateTime => 10,
                DataKind.Time => 8,
                DataKind.Duration => 8,
                DataKind.Uuid => 16,
                DataKind.String => 2L * value.AsString().Length,
                DataKind.JsonValue => 2L * value.AsJsonValue().Length,
                DataKind.Vector => 4L * value.AsVector().Length,
                DataKind.Matrix => 4L * value.AsMatrix(out _, out _).Length,
                DataKind.Tensor => 4L * value.AsTensor(out _).Length,
                DataKind.UInt8Array => value.AsUInt8Array().Length,
                DataKind.Image => value.AsImage().Length,
                DataKind.Array => EstimateArrayBytes(value),
                _ => 8,
            };
        }

        bytes += DictionaryEntryOverheadBytes;

        return bytes;
    }

    private static long EstimateArrayBytes(DataValue value)
    {
        DataValue[] elements = value.AsArray();
        long total = 16; // Array object overhead + length.
        foreach (DataValue element in elements)
        {
            total += DataValueOverheadBytes + 8; // Conservative per-element estimate.
        }

        return total;
    }
}
