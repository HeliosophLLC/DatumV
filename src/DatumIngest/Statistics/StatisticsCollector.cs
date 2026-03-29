namespace DatumIngest.Statistics;

using DatumIngest.Model;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Manages per-column statistic accumulators and collects statistics from rows.
/// </summary>
public sealed class StatisticsCollector
{
    private readonly Dictionary<string, List<IStatisticAccumulator>> _columnAccumulators = new();
    private readonly int _topK;

    /// <summary>
    /// Cached accumulator lists indexed by column ordinal. Populated on the first row
    /// and reused for all subsequent rows, bypassing the dictionary lookup on the hot path.
    /// </summary>
    private List<IStatisticAccumulator>[]? _ordinalAccumulators;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsCollector"/> class.
    /// </summary>
    /// <param name="topK">Number of top-K values to track per column. Defaults to 10.</param>
    public StatisticsCollector(int topK = 10)
    {
        _topK = topK;
    }

    /// <summary>
    /// Adds a batch of rows to the statistics collector. Each row is processed sequentially.
    /// </summary>
    /// <param name="rowBatch">The row batch to process.</param>
    /// <param name="store">
    /// Optional override store used to resolve DataValue offsets. When provided, the
    /// caller guarantees this store outlives the current row group — accumulators that
    /// retain DataValues (e.g. <see cref="Accumulators.SpaceSavingAccumulator"/>) use
    /// it to materialize strings at merge time. When <c>null</c>, falls back to the
    /// batch's own arena (which is reset when the batch returns to the pool).
    /// </param>
    public void Collect(RowBatch rowBatch, IValueStore? store = null)
    {
        IValueStore resolvedStore = store ?? rowBatch.Arena;
        for (int i = 0; i < rowBatch.Count; i++)
        {
            AddRow(rowBatch[i], resolvedStore);
        }
    }

    /// <summary>
    /// Adds a row to all column accumulators. Creates accumulators for new columns on first encounter.
    /// </summary>
    /// <param name="row">The row to accumulate.</param>
    /// <param name="store">Value store for resolving reference-type payloads.</param>
    public void AddRow(Row row, IValueStore store)
    {
        List<IStatisticAccumulator>[]? cached = _ordinalAccumulators;

        if (cached is not null && cached.Length == row.FieldCount)
        {
            for (int ordinal = 0; ordinal < cached.Length; ordinal++)
            {
                DataValue value = row[ordinal];
                List<IStatisticAccumulator> accumulators = cached[ordinal];

                foreach (IStatisticAccumulator accumulator in accumulators)
                {
                    accumulator.Add(value, store);
                }
            }

            return;
        }

        // First row or schema change: populate via the dictionary path and cache the result.
        List<IStatisticAccumulator>[] ordinalCache = new List<IStatisticAccumulator>[row.FieldCount];
        int index = 0;

        foreach (string columnName in row.ColumnNames)
        {
            DataValue value = row[columnName];
            if (!_columnAccumulators.TryGetValue(columnName, out List<IStatisticAccumulator>? accumulators))
            {
                accumulators = CreateAccumulators(value);
                _columnAccumulators[columnName] = accumulators;
            }

            foreach (IStatisticAccumulator accumulator in accumulators)
            {
                accumulator.Add(value, store);
            }

            ordinalCache[index] = accumulators;
            index++;
        }

        _ordinalAccumulators = ordinalCache;
    }

    /// <summary>
    /// Gets the aggregated statistics for all columns.
    /// </summary>
    public IReadOnlyDictionary<string, ColumnStatistics> GetStatistics()
    {
        Dictionary<string, ColumnStatistics> result = new();

        foreach (KeyValuePair<string, List<IStatisticAccumulator>> entry in _columnAccumulators)
        {
            Dictionary<string, StatisticResult> results = new();

            foreach (IStatisticAccumulator accumulator in entry.Value)
            {
                foreach (StatisticResult statisticResult in accumulator.GetResults())
                {
                    results[statisticResult.Name] = statisticResult;
                }
            }

            result[entry.Key] = new ColumnStatistics(entry.Key, results);
        }

        return result;
    }

    /// <summary>
    /// Notifies every accumulator that the writer is about to flush a row
    /// group and reset its arena. Accumulators that hold arena-relative
    /// references must materialize them here.
    /// </summary>
    /// <param name="writerArenaStore">Read-only view over the row group's page of the writer's arena.</param>
    public void FlushRowGroup(IValueStore writerArenaStore)
    {
        foreach (List<IStatisticAccumulator> accumulators in _columnAccumulators.Values)
        {
            foreach (IStatisticAccumulator accumulator in accumulators)
            {
                accumulator.BeforeRowGroupFlush(writerArenaStore);
            }
        }
    }

    private List<IStatisticAccumulator> CreateAccumulators(DataValue firstValue)
    {
        DataKind kind = firstValue.Kind;
        // Sidecar-backed image columns get their dimension/size summaries from the
        // sibling derived columns (file_width / file_height / file_channels /
        // file_byte_length / file_orientation) emitted by the deserializer, so the
        // ImageStatsAccumulator would just emit an empty image_stats record.
        // Skip it entirely. The first-row check is sufficient because the
        // deserializer makes a one-shot routing decision for the whole column —
        // if the first non-null value is sidecar-backed, every value is.
        bool sidecarBacked = firstValue.IsInSidecar;

        // Typed-array columns (Kind + IsArray flag set on values). Covers byte arrays
        // (UInt8 + IsArray), vector arrays (Float32 + IsArray, the former Vector kind),
        // and any other typed-array column. Numeric/cardinality accumulators must not
        // fire for these; their element-level stats are handled by dedicated array
        // accumulators where defined.
        bool isArrayValue = firstValue.IsArray;
        bool isByteArray = firstValue.IsByteArrayKind;

        List<IStatisticAccumulator> accumulators =
        [
            new CountAccumulator(),
            new MissingRunsAccumulator()
        ];

        // Cardinality and top-K are skipped for binary/multi-dim kinds that have no
        // useful distinct-value or frequency semantics. Images, byte blobs, JSON
        // documents, typed arrays, and structs are treated as opaque payloads — HLL
        // on their arena offsets would just return the row count. JSON shape stats
        // come from JsonAccumulator instead. Perceptual-hash cardinality is
        // available on demand via the phash() SQL function.
        if (!isArrayValue
            && kind is not (DataKind.Image or DataKind.Struct or DataKind.Json))
        {
            accumulators.Add(new CardinalityAccumulator());
            accumulators.Add(new SpaceSavingAccumulator(_topK, kind));
        }

        // Decimal columns use a dedicated accumulator that stays in decimal
        // arithmetic for full 28-digit precision; NumericAccumulator would
        // collapse to double via TryToDouble and lose precision past 2^53.
        // Histogram + Quantile aren't wired for decimal yet (their internals
        // are double-based) — diagnostic-only stats, deferred to a follow-up.
        if (!isArrayValue && kind == DataKind.Decimal)
        {
            accumulators.Add(new DecimalAccumulator());
        }
        // IsNumericScalar returns true for UInt8/Float32/etc. — but a typed-array
        // value (UInt8 + IsArray, Float32 + IsArray) is not a scalar and must not
        // get numeric stats. Gate on the IsArray flag explicitly.
        else if (!isArrayValue && DataValueComparer.IsNumericScalar(kind))
        {
            accumulators.Add(new NumericAccumulator());
            accumulators.Add(new HistogramAccumulator());
            accumulators.Add(new QuantileAccumulator());
        }

        if (kind == DataKind.String)
        {
            accumulators.Add(new StringLengthAccumulator());
        }

        // Array stats accumulator covers any non-byte-array typed numeric array —
        // Float16/32/64 and the signed/unsigned integer family up to 64-bit.
        // UInt8+IsArray is intentionally a byte blob (BinarySizeAccumulator path,
        // gated separately above); Decimal/Int128/UInt128 arrays are not yet
        // supported (precision / no array payload). See ArrayStatsAccumulator
        // class doc for the exact element-kind matrix.
        if (isArrayValue && !isByteArray && IsArrayElementKindSupported(kind))
        {
            accumulators.Add(new ArrayStatsAccumulator());
        }

        if (kind is DataKind.Image && !sidecarBacked)
        {
            accumulators.Add(new ImageStatsAccumulator());
        }

        if (isByteArray)
        {
            accumulators.Add(new BinarySizeAccumulator());
        }

        if (kind is DataKind.Date or DataKind.Timestamp or DataKind.TimestampTz or DataKind.Time)
        {
            accumulators.Add(new TemporalRangeAccumulator());
        }

        if (kind is DataKind.Duration)
        {
            accumulators.Add(new NumericAccumulator());
            accumulators.Add(new HistogramAccumulator());
            accumulators.Add(new QuantileAccumulator());
        }

        if (kind is DataKind.Uuid)
        {
            accumulators.Add(new UuidAccumulator());
        }

        if (kind is DataKind.Json)
        {
            accumulators.Add(new JsonAccumulator());
        }

        return accumulators;
    }

    private static bool IsArrayElementKindSupported(DataKind kind) =>
        kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64;
}
