using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Indexing.Bloom;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Builds a <see cref="SourceIndex"/> in a single pass over all rows from a table provider.
/// Produces the unified-sidecar payload — fingerprint, schema, chunk directory + zone maps,
/// bloom filters, bitmap indexes. Per-column B+Tree acceleration lives outside this builder
/// in companion <c>.datum-bptree-{col}</c> files written directly by
/// <see cref="Heliosoph.DatumV.Ingestion.Indexer"/>.
/// </summary>
public sealed class SourceIndexBuilder
{
    private readonly int _chunkSize;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly bool _bloomAllColumns;
    private readonly bool _computeCardinality;

    /// <summary>
    /// Creates a builder with the specified chunk size and an explicit bloom-column set.
    /// </summary>
    public SourceIndexBuilder(
        int chunkSize = IndexConstants.DefaultChunkSize,
        IReadOnlySet<string>? bloomColumns = null,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _bloomColumns = bloomColumns;
        _bloomAllColumns = false;
        _computeCardinality = computeCardinality;
    }

    /// <summary>
    /// Creates a builder that discovers columns from the data and optionally builds bloom
    /// filters for every one of them.
    /// </summary>
    public SourceIndexBuilder(
        bool bloomAllColumns,
        int chunkSize = IndexConstants.DefaultChunkSize,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _bloomColumns = null;
        _bloomAllColumns = bloomAllColumns;
        _computeCardinality = computeCardinality;
    }

    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing.
    /// </summary>
    public IncrementalIndexBuilder CreateIncrementalBuilder(SourceFingerprint fingerprint)
    {
        return new IncrementalIndexBuilder(
            _chunkSize, fingerprint,
            _bloomColumns,
            _bloomAllColumns,
            _computeCardinality);
    }

    /// <summary>
    /// Resolves the effective column set for bloom indexes. When the "all columns" flag is
    /// set, returns a set of all column names from the discovered schema.
    /// </summary>
    internal static IReadOnlySet<string>? ResolveEffectiveColumns(
        IReadOnlySet<string>? explicitColumns, bool allColumns, Schema schema)
    {
        if (allColumns)
        {
            HashSet<string> allColumnNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (ColumnInfo column in schema.Columns)
            {
                allColumnNames.Add(column.Name);
            }

            return allColumnNames;
        }

        return explicitColumns;
    }

    /// <summary>
    /// Selects columns eligible for sorted-key acceleration based on their <see cref="DataKind"/>.
    /// Compact types (numeric, date/time, boolean, UUID) are always included. String columns
    /// are included tentatively. Wide types are excluded.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Heliosoph.DatumV.Ingestion.Indexer"/> to decide which columns get a
    /// per-column <c>.datum-bptree-{col}</c> companion file. Bitmap-eligible columns
    /// (cardinality up to 256) are a subset of this set; the bitmap accumulator decides
    /// dynamically whether to keep or abandon based on observed cardinality.
    /// </remarks>
    public static IReadOnlySet<string> ResolveAutoIndexColumns(Schema schema)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            if (IsAutoIndexableKind(column.Kind))
            {
                columns.Add(column.Name);
            }
        }

        return columns;
    }

    /// <summary>
    /// Returns whether a column of the specified kind is eligible for sorted-key acceleration.
    /// </summary>
    public static bool IsAutoIndexableKind(DataKind kind)
    {
        return kind is DataKind.Float32
            or DataKind.Float64
            or DataKind.UInt8
            or DataKind.Int8
            or DataKind.Int16
            or DataKind.UInt16
            or DataKind.Int32
            or DataKind.UInt32
            or DataKind.Int64
            or DataKind.UInt64
            or DataKind.Boolean
            or DataKind.Date
            or DataKind.Timestamp
            or DataKind.TimestampTz
            or DataKind.Time
            or DataKind.Duration
            or DataKind.Uuid
            or DataKind.String;
    }

    internal static Dictionary<string, ChunkAccumulator> CreateAccumulators(Row row, bool computeCardinality = true)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in row.ColumnNames)
        {
            accumulators[name] = new ChunkAccumulator(computeCardinality);
        }

        return accumulators;
    }

    internal static Dictionary<string, ChunkAccumulator> CreateAccumulators(Schema schema, bool computeCardinality = true)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            accumulators[column.Name] = new ChunkAccumulator(computeCardinality);
        }

        return accumulators;
    }

    /// <summary>
    /// Creates bloom filters for the specified columns, sized for the chunk capacity.
    /// </summary>
    internal static Dictionary<string, BloomFilter>? CreateBloomFilters(
        IReadOnlySet<string>? bloomColumns, int expectedElements)
    {
        if (bloomColumns is null || bloomColumns.Count == 0)
        {
            return null;
        }

        Dictionary<string, BloomFilter> filters = new(StringComparer.OrdinalIgnoreCase);

        foreach (string column in bloomColumns)
        {
            filters[column] = new BloomFilter(expectedElements);
        }

        return filters;
    }

    /// <summary>
    /// Assembles per-chunk bloom filter dictionaries into a <see cref="BloomFilterSet"/>.
    /// </summary>
    internal static BloomFilterSet BuildBloomFilterSet(
        List<Dictionary<string, BloomFilter>> chunkBloomFilters, int chunkCount)
    {
        HashSet<string> columnNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (Dictionary<string, BloomFilter> chunkFilters in chunkBloomFilters)
        {
            foreach (string name in chunkFilters.Keys)
            {
                columnNames.Add(name);
            }
        }

        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase);

        foreach (string columnName in columnNames)
        {
            BloomFilter[] columnFilters = new BloomFilter[chunkCount];

            for (int i = 0; i < chunkBloomFilters.Count && i < chunkCount; i++)
            {
                if (chunkBloomFilters[i].TryGetValue(columnName, out BloomFilter? filter))
                {
                    columnFilters[i] = filter;
                }
                else
                {
                    columnFilters[i] = new BloomFilter(1);
                }
            }

            filters[columnName] = columnFilters;
        }

        return new BloomFilterSet(filters, chunkCount);
    }

    /// <summary>
    /// Creates a <see cref="BitmapChunkAccumulator"/> for each bitmap-eligible column in
    /// the schema. Returns <c>null</c> if no columns are eligible.
    /// </summary>
    internal static Dictionary<string, BitmapChunkAccumulator>? CreateBitmapAccumulators(Schema schema)
    {
        Dictionary<string, BitmapChunkAccumulator>? accumulators = null;

        foreach (ColumnInfo column in schema.Columns)
        {
            if (IsAutoIndexableKind(column.Kind) && column.IsArray == false)
            {
                accumulators ??= new Dictionary<string, BitmapChunkAccumulator>(StringComparer.OrdinalIgnoreCase);
                accumulators[column.Name] = new BitmapChunkAccumulator();
            }
        }

        return accumulators;
    }

    /// <summary>
    /// Builds a <see cref="BitmapIndexSet"/> from per-column accumulators, keeping only
    /// columns whose cardinality stayed within the bitmap threshold.
    /// </summary>
    internal static BitmapIndexSet? BuildBitmapIndexSet(
        Dictionary<string, BitmapChunkAccumulator> accumulators)
    {
        Dictionary<string, BitmapColumnIndex>? indexes = null;

        foreach (KeyValuePair<string, BitmapChunkAccumulator> entry in accumulators)
        {
            BitmapColumnIndex? columnIndex = entry.Value.Build();

            if (columnIndex is not null)
            {
                indexes ??= new Dictionary<string, BitmapColumnIndex>(StringComparer.OrdinalIgnoreCase);
                indexes[entry.Key] = columnIndex;
            }
        }

        return indexes is not null ? new BitmapIndexSet(indexes) : null;
    }
}
