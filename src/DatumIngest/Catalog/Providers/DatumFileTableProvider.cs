using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads <c>.datum</c> native column-store files via the <see cref="DatumFileReader"/>.
/// Supports projection pushdown and zone-map-based row group pruning when a filter hint
/// is provided by the query engine.
/// </summary>
public sealed class DatumFileTableProvider : ITableProvider, IFilterableTableProvider
{
    /// <summary>Total number of row groups examined in the most recent read.</summary>
    public int TotalRowGroups { get; private set; }

    /// <summary>Number of row groups skipped by zone-map pruning in the most recent read.</summary>
    public int PrunedRowGroups { get; private set; }

    /// <inheritdoc/>
    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        return Task.FromResult(reader.Schema);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
        => OpenCoreAsync(descriptor, requiredColumns, filterHint: null, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        CancellationToken cancellationToken)
        => OpenCoreAsync(descriptor, requiredColumns, filterHint, cancellationToken);

    /// <inheritdoc/>
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        return Task.FromResult(new ProviderCapabilities(
            reader.TotalRowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }

    private async IAsyncEnumerable<Row> OpenCoreAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using DatumFileReader reader = DatumFileReader.Open(descriptor.FilePath);
        Schema schema = reader.Schema;

        // Resolve which column indices to decode (projection pushdown).
        int[] projectedIndices = ResolveProjection(schema, requiredColumns);
        string[] projectedNames = Array.ConvertAll(projectedIndices, i => schema.Columns[i].Name);

        Dictionary<string, int> nameIndex = new(projectedNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < projectedNames.Length; i++)
        {
            nameIndex[projectedNames[i]] = i;
        }

        // Collect the column names referenced in the filter to limit statistics construction.
        HashSet<string>? filterColumnNames = null;
        if (filterHint is not null)
        {
            filterColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filterHint))
            {
                filterColumnNames.Add(columnName);
            }
        }

        TotalRowGroups = reader.RowGroupCount;
        PrunedRowGroups = 0;

        for (int rgIndex = 0; rgIndex < reader.RowGroupCount; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatumRowGroupDescriptor rowGroupDescriptor = reader.GetRowGroupDescriptor(rgIndex);

            // Zone map pruning: only attempted when a filter hint was provided.
            if (filterHint is not null && filterColumnNames is not null)
            {
                Dictionary<string, ColumnStatisticsRange> statistics =
                    BuildStatistics(schema, rowGroupDescriptor, filterColumnNames);

                if (StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics))
                {
                    PrunedRowGroups++;
                    continue;
                }
            }

            int rowCount = (int)rowGroupDescriptor.RowCount;
            DataValue[][] columns = reader.ReadColumns(rgIndex, projectedIndices);

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = new DataValue[projectedIndices.Length];
                for (int colPos = 0; colPos < projectedIndices.Length; colPos++)
                {
                    values[colPos] = columns[colPos][rowIndex];
                }

                yield return new Row(projectedNames, values, nameIndex);
            }
        }
    }

    private static int[] ResolveProjection(Schema schema, IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            int[] all = new int[schema.Columns.Count];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }

        List<int> projected = new(requiredColumns.Count);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (requiredColumns.Contains(schema.Columns[i].Name))
            {
                projected.Add(i);
            }
        }

        return projected.ToArray();
    }

    private static Dictionary<string, ColumnStatisticsRange> BuildStatistics(
        Schema schema,
        DatumRowGroupDescriptor rowGroup,
        HashSet<string> filterColumnNames)
    {
        Dictionary<string, ColumnStatisticsRange> statistics =
            new(filterColumnNames.Count, StringComparer.OrdinalIgnoreCase);

        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            string columnName = schema.Columns[columnIndex].Name;
            if (!filterColumnNames.Contains(columnName)) continue;

            DatumZoneMap zoneMap = rowGroup.ColumnChunks[columnIndex].ZoneMap;
            statistics[columnName] = new ColumnStatisticsRange(
                zoneMap.Minimum,
                zoneMap.Maximum,
                zoneMap.NullCount,
                rowGroup.RowCount);
        }

        return statistics;
    }
}
