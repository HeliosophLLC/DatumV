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
    internal const long DataValueOverheadBytes = 20;

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

    /// <summary>
    /// Records every row in <paramref name="batch"/>: samples those for which
    /// <see cref="ShouldSample"/> returns <see langword="true"/> and increments the running
    /// row count for every row. Encapsulates the per-row cadence so per-batch callers don't
    /// have to inline the loop, and keeps <see cref="EscalateToEveryRow"/> meaningful.
    /// </summary>
    internal void RecordBatch(RowBatch batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            if (ShouldSample())
            {
                RecordSample(batch[i]);
            }
            IncrementRowCount();
        }
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
            DataValue field = row[index];
            // Any array-kinded value (legacy UInt8Array/Vector/Matrix/Tensor/Array
            // OR new-model Kind+IsArray) is variable-width.
            if (field.IsArray) return false;

            DataKind kind = field.Kind;
            if (kind is DataKind.String or DataKind.Image
                or DataKind.JsonValue or DataKind.Struct)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Estimates the memory consumed by a single row in a hash-based collection.</summary>
    /// <remarks>
    /// Per-row cost is the per-DataValue overhead plus each value's variable-length
    /// payload byte count (read in O(1) via <see cref="DataValue.ContentByteLength"/> —
    /// no arena materialization). Sidecar-backed values contribute zero in-memory
    /// residency since their bytes live on disk.
    /// </remarks>
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

            // ContentByteLength returns 0 for inline scalars (already counted by the
            // DataValue overhead), 0 for sidecar values (on-disk), and the payload
            // byte length for arena-backed strings, byte arrays, vectors, matrices,
            // tensors, and images.
            bytes += value.ContentByteLength;

            if (value.Kind == DataKind.Array)
            {
                bytes += EstimateArrayBytes(value);
            }
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
