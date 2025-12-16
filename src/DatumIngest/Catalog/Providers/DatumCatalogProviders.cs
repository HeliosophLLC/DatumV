using System.Runtime.CompilerServices;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Manifest;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table listing all registered functions.
/// Query with <c>SELECT * FROM datum_catalog.functions</c>.
/// </summary>
/// <remarks>
/// Schema (7 columns): function_name, function_type, category, return_type,
/// description, parameter_count, query_unit_cost.
/// One row per function name, including aliases as separate rows.
/// </remarks>
internal sealed class DatumCatalogFunctionsProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.functions";

    private static readonly Schema _schema = BuildSchema();

    private readonly FunctionRegistry _registry;

    /// <summary>Creates a provider backed by the given function registry.</summary>
    internal DatumCatalogFunctionsProvider(Pool pool, FunctionRegistry registry) : base(pool, TableName)
    {
        _registry = registry;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (FunctionDescriptor descriptor in _registry.ScalarDescriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            FillScalarRow(values, descriptor, batch.Arena);
            batch.Add(values);
            if (batch.IsFull) { yield return batch; batch = null; }

            foreach (string alias in descriptor.Aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] aliasValues = Pool.RentDataValues(_schema.Columns.Count);
                FillScalarRow(aliasValues, descriptor with { PrimaryName = alias }, batch.Arena);
                batch.Add(aliasValues);
                if (batch.IsFull) { yield return batch; batch = null; }
            }
        }

        foreach (string name in _registry.AggregateFunctionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            FillMinimalRow(values, name, "AGGREGATE", batch.Arena);
            batch.Add(values);
            if (batch.IsFull) { yield return batch; batch = null; }
        }

        foreach (string name in _registry.TableValuedFunctionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            FillMinimalRow(values, name, "TABLE_VALUED", batch.Arena);
            batch.Add(values);
            if (batch.IsFull) { yield return batch; batch = null; }
        }

        foreach (string name in _registry.WindowFunctionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
            DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
            FillMinimalRow(values, name, "WINDOW", batch.Arena);
            batch.Add(values);
            if (batch.IsFull) { yield return batch; batch = null; }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void FillScalarRow(DataValue[] cells, FunctionDescriptor descriptor, Arena arena)
    {
        FunctionSignatureVariant? first = descriptor.Signatures.Count > 0 ? descriptor.Signatures[0] : null;
        string? returnType = first?.ReturnType.Describe();
        int? parameterCount = first is null
            ? null
            : first.Parameters.Count + (first.VariadicTrailing is null ? 0 : 1);

        cells[0] = DataValue.FromString(descriptor.PrimaryName, arena);
        cells[1] = DataValue.FromString("SCALAR", arena);
        cells[2] = DataValue.FromString(descriptor.Category.ToString(), arena);
        cells[3] = returnType is not null ? DataValue.FromString(returnType, arena) : DataValue.Null(DataKind.String);
        cells[4] = DataValue.FromString(descriptor.Description, arena);
        cells[5] = parameterCount.HasValue ? DataValue.FromInt32(parameterCount.Value) : DataValue.Null(DataKind.Int32);
        cells[6] = DataValue.Null(DataKind.Int32);
    }

    private static void FillMinimalRow(DataValue[] cells, string name, string functionType, Arena arena)
    {
        cells[0] = DataValue.FromString(name, arena);
        cells[1] = DataValue.FromString(functionType, arena);
        cells[2] = DataValue.Null(DataKind.String);
        cells[3] = DataValue.Null(DataKind.String);
        cells[4] = DataValue.Null(DataKind.String);
        cells[5] = DataValue.Null(DataKind.Int32);
        cells[6] = DataValue.Null(DataKind.Int32);
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("function_name",   DataKind.String, nullable: false),
        new ColumnInfo("function_type",   DataKind.String, nullable: false),
        new ColumnInfo("category",        DataKind.String, nullable: true),
        new ColumnInfo("return_type",     DataKind.String, nullable: true),
        new ColumnInfo("description",     DataKind.String, nullable: true),
        new ColumnInfo("parameter_count", DataKind.Int32,  nullable: true),
        new ColumnInfo("query_unit_cost", DataKind.Int32,  nullable: true),
    ]);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Virtual table listing function parameters for all scalar functions.
/// Query with <c>SELECT * FROM datum_catalog.function_parameters</c>.
/// </summary>
/// <remarks>
/// Schema (5 columns): function_name, ordinal_position, parameter_name,
/// data_type, is_optional. Only scalar functions with declared signatures
/// are included; aggregate and TVF parameters are not surfaced here.
/// </remarks>
internal sealed class DatumCatalogFunctionParametersProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.function_parameters";

    private static readonly Schema _schema = BuildSchema();

    private readonly FunctionRegistry _registry;

    /// <summary>Creates a provider backed by the given function registry.</summary>
    internal DatumCatalogFunctionParametersProvider(Pool pool, FunctionRegistry registry) : base(pool, TableName)
    {
        _registry = registry;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (FunctionDescriptor descriptor in _registry.ScalarDescriptors)
        {
            if (descriptor.Signatures.Count == 0)
            {
                continue;
            }

            FunctionSignatureVariant variant = descriptor.Signatures[0];

            for (int ordinal = 0; ordinal < variant.Parameters.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                ParameterSpec parameter = variant.Parameters[ordinal];
                DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                FillParameterRow(values, descriptor.PrimaryName, ordinal + 1, parameter.Name, parameter.Kind.Describe(),
                    parameter.IsOptional ? "YES" : "NO", batch.Arena);
                batch.Add(values);
                if (batch.IsFull) { yield return batch; batch = null; }
            }

            if (variant.VariadicTrailing is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                FillParameterRow(values, descriptor.PrimaryName, variant.Parameters.Count + 1,
                    variant.VariadicTrailing.Name, variant.VariadicTrailing.Kind.Describe() + "...",
                    "VARIADIC", batch.Arena);
                batch.Add(values);
                if (batch.IsFull) { yield return batch; batch = null; }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void FillParameterRow(
        DataValue[] cells, string functionName, int ordinal, string paramName,
        string dataType, string isOptional, Arena arena)
    {
        cells[0] = DataValue.FromString(functionName, arena);
        cells[1] = DataValue.FromInt32(ordinal);
        cells[2] = DataValue.FromString(paramName, arena);
        cells[3] = DataValue.FromString(dataType, arena);
        cells[4] = DataValue.FromString(isOptional, arena);
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("function_name",    DataKind.String, nullable: false),
        new ColumnInfo("ordinal_position", DataKind.Int32,  nullable: false),
        new ColumnInfo("parameter_name",   DataKind.String, nullable: false),
        new ColumnInfo("data_type",        DataKind.String, nullable: false),
        new ColumnInfo("is_optional",      DataKind.String, nullable: false),
    ]);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Virtual table listing per-column statistics for all tables that have manifests.
/// Query with <c>SELECT * FROM datum_catalog.statistics</c>.
/// </summary>
/// <remarks>
/// Rows are sourced from <see cref="ITableProvider.GetManifest"/>. Providers
/// without a manifest (virtual tables, non-analysed sources) produce no rows.
/// </remarks>
internal sealed class DatumCatalogStatisticsProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.statistics";

    private static readonly Schema _schema = BuildSchema();

    private readonly TableCatalog _catalog;

    /// <summary>Creates a provider that reflects statistics from all registered tables.</summary>
    internal DatumCatalogStatisticsProvider(Pool pool, TableCatalog catalog) : base(pool, TableName)
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        ITableProvider[] snapshot = [.. _catalog];

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (ITableProvider provider in snapshot)
        {
            QueryResultsManifest? manifest = provider.GetManifest();
            if (manifest is null)
            {
                continue;
            }

            foreach (FeatureManifest feature in manifest.Features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                FillStatisticsRow(values, provider.Name, feature, manifest.RowCount, batch.Arena);
                batch.Add(values);
                if (batch.IsFull) { yield return batch; batch = null; }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void FillStatisticsRow(
        DataValue[] cells, string tableName, FeatureManifest feature, long rowCount, Arena arena)
    {
        (string? minValue, string? maxValue) = ExtractMinMax(feature);
        string? topValue = feature.TopKValues.Count > 0 ? feature.TopKValues[0].Value : null;
        long? topFrequency = feature.TopKValues.Count > 0 ? feature.TopKValues[0].Frequency : null;

        NumericFeatureManifest? numeric = feature as NumericFeatureManifest;
        QuantileData? quantiles = numeric?.Quantiles;
        StringFeatureManifest? text = feature as StringFeatureManifest;
        BooleanFeatureManifest? boolean = feature as BooleanFeatureManifest;

        cells[0]  = DataValue.FromString(tableName, arena);
        cells[1]  = DataValue.FromString(feature.Name, arena);
        cells[2]  = DataValue.FromString(feature.Kind.ToString(), arena);
        cells[3]  = DataValue.FromInt64(rowCount);
        cells[4]  = DataValue.FromInt64(feature.EstimatedDistinctCount);
        cells[5]  = NullableFloat64(feature.NullRatio);
        cells[6]  = NullableString(minValue, arena);
        cells[7]  = NullableString(maxValue, arena);
        cells[8]  = NullableFloat64(feature.Entropy);
        cells[9]  = NullableFloat64(feature.DominantValueRatio);
        cells[10] = DataValue.FromString(feature.IsConstant ? "YES" : "NO", arena);
        cells[11] = feature.Role.HasValue
            ? DataValue.FromString(feature.Role.Value.ToString(), arena)
            : DataValue.Null(DataKind.String);
        cells[12] = NullableString(topValue, arena);
        cells[13] = topFrequency.HasValue ? DataValue.FromInt64(topFrequency.Value) : DataValue.Null(DataKind.Int64);
        cells[14] = numeric is not null ? DataValue.FromFloat64(numeric.Mean)              : DataValue.Null(DataKind.Float64);
        cells[15] = numeric is not null ? DataValue.FromFloat64(numeric.StandardDeviation) : DataValue.Null(DataKind.Float64);
        cells[16] = numeric is not null ? DataValue.FromFloat64(numeric.Skewness)          : DataValue.Null(DataKind.Float64);
        cells[17] = numeric is not null ? DataValue.FromFloat64(numeric.Kurtosis)          : DataValue.Null(DataKind.Float64);
        cells[18] = quantiles is not null ? DataValue.FromFloat64(quantiles.P25)           : DataValue.Null(DataKind.Float64);
        cells[19] = quantiles is not null ? DataValue.FromFloat64(quantiles.P50)           : DataValue.Null(DataKind.Float64);
        cells[20] = quantiles is not null ? DataValue.FromFloat64(quantiles.P75)           : DataValue.Null(DataKind.Float64);
        cells[21] = numeric is not null ? DataValue.FromFloat64(numeric.ZeroRatio)         : DataValue.Null(DataKind.Float64);
        cells[22] = numeric is not null ? DataValue.FromFloat64(numeric.OutlierRatio)      : DataValue.Null(DataKind.Float64);
        cells[23] = numeric is not null
            ? DataValue.FromString(numeric.IntegerValued ? "YES" : "NO", arena)
            : DataValue.Null(DataKind.String);
        cells[24] = text is not null ? DataValue.FromInt32(text.MinLength)    : DataValue.Null(DataKind.Int32);
        cells[25] = text is not null ? DataValue.FromInt32(text.MaxLength)    : DataValue.Null(DataKind.Int32);
        cells[26] = boolean is not null ? DataValue.FromFloat64(boolean.TrueRatio) : DataValue.Null(DataKind.Float64);
    }

    private static (string? Min, string? Max) ExtractMinMax(FeatureManifest feature) => feature switch
    {
        NumericFeatureManifest numeric => (numeric.Min.ToString("G"), numeric.Max.ToString("G")),
        StringFeatureManifest text => (text.MinLength.ToString(), text.MaxLength.ToString()),
        TemporalFeatureManifest temporal => (temporal.Earliest, temporal.Latest),
        _ => (null, null),
    };

    private static DataValue NullableFloat64(double? value) =>
        value.HasValue ? DataValue.FromFloat64(value.Value) : DataValue.Null(DataKind.Float64);

    private static DataValue NullableString(string? value, Arena arena) =>
        value is not null ? DataValue.FromString(value, arena) : DataValue.Null(DataKind.String);

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("table_name",           DataKind.String,  nullable: false),
        new ColumnInfo("column_name",          DataKind.String,  nullable: false),
        new ColumnInfo("data_type",            DataKind.String,  nullable: false),
        new ColumnInfo("row_count",            DataKind.Int64,   nullable: false),
        new ColumnInfo("distinct_count",       DataKind.Int64,   nullable: false),
        new ColumnInfo("null_ratio",           DataKind.Float64, nullable: true),
        new ColumnInfo("min_value",            DataKind.String,  nullable: true),
        new ColumnInfo("max_value",            DataKind.String,  nullable: true),
        new ColumnInfo("entropy",              DataKind.Float64, nullable: true),
        new ColumnInfo("dominant_value_ratio", DataKind.Float64, nullable: true),
        new ColumnInfo("is_constant",          DataKind.String,  nullable: false),
        new ColumnInfo("column_role",          DataKind.String,  nullable: true),
        new ColumnInfo("top_value",            DataKind.String,  nullable: true),
        new ColumnInfo("top_value_frequency",  DataKind.Int64,   nullable: true),
        new ColumnInfo("mean",                 DataKind.Float64, nullable: true),
        new ColumnInfo("standard_deviation",   DataKind.Float64, nullable: true),
        new ColumnInfo("skewness",             DataKind.Float64, nullable: true),
        new ColumnInfo("kurtosis",             DataKind.Float64, nullable: true),
        new ColumnInfo("p25",                  DataKind.Float64, nullable: true),
        new ColumnInfo("p50",                  DataKind.Float64, nullable: true),
        new ColumnInfo("p75",                  DataKind.Float64, nullable: true),
        new ColumnInfo("zero_ratio",           DataKind.Float64, nullable: true),
        new ColumnInfo("outlier_ratio",        DataKind.Float64, nullable: true),
        new ColumnInfo("integer_valued",       DataKind.String,  nullable: true),
        new ColumnInfo("min_length",           DataKind.Int32,   nullable: true),
        new ColumnInfo("max_length",           DataKind.Int32,   nullable: true),
        new ColumnInfo("true_ratio",           DataKind.Float64, nullable: true),
    ]);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Virtual table listing per-column index metadata for all indexed tables.
/// Query with <c>SELECT * FROM datum_catalog.indexes</c>.
/// </summary>
/// <remarks>
/// Rows are sourced from <see cref="ITableProvider.GetSourceIndex"/>. Providers
/// without a source index produce no rows.
/// </remarks>
internal sealed class DatumCatalogIndexesProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.indexes";

    private static readonly Schema _schema = BuildSchema();

    private readonly TableCatalog _catalog;

    /// <summary>Creates a provider that reflects index metadata from all registered tables.</summary>
    internal DatumCatalogIndexesProvider(Pool pool, TableCatalog catalog) : base(pool, TableName)
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        ITableProvider[] snapshot = [.. _catalog];

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (ITableProvider provider in snapshot)
        {
            SourceIndex? sourceIndex = provider.GetSourceIndex();
            if (sourceIndex is null)
            {
                continue;
            }

            string tableName = provider.Name;
            int chunkCount = sourceIndex.Chunks.Count;
            long totalRowCount = sourceIndex.Schema.TotalRowCount;

            if (sourceIndex.MappedSortedIndexes is not null)
            {
                foreach (KeyValuePair<string, SortedIndex> entry in sourceIndex.MappedSortedIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                    FillIndexRow(values, tableName, entry.Key, "SORTED", entry.Value.EntryCount, chunkCount, totalRowCount, batch.Arena);
                    batch.Add(values);
                    if (batch.IsFull) { yield return batch; batch = null; }
                }
            }

            if (sourceIndex.BPlusTreeIndexes is not null)
            {
                foreach (string columnName in sourceIndex.BPlusTreeIndexes.ColumnNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    long? entryCount = sourceIndex.BPlusTreeIndexes.TryGetIndex(columnName, out BPlusTreeColumnIndex? btree)
                        ? btree.EntryCount
                        : null;
                    DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                    FillIndexRow(values, tableName, columnName, "BTREE", entryCount, chunkCount, totalRowCount, batch.Arena);
                    batch.Add(values);
                    if (batch.IsFull) { yield return batch; batch = null; }
                }
            }

            if (sourceIndex.BitmapIndexes is not null)
            {
                foreach (string columnName in sourceIndex.BitmapIndexes.ColumnNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                    FillIndexRow(values, tableName, columnName, "BITMAP", entryCount: null, chunkCount, totalRowCount, batch.Arena);
                    batch.Add(values);
                    if (batch.IsFull) { yield return batch; batch = null; }
                }
            }

            if (sourceIndex.BloomFilters is not null)
            {
                foreach (string columnName in sourceIndex.BloomFilters.ColumnNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                    DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                    FillIndexRow(values, tableName, columnName, "BLOOM", entryCount: null, chunkCount, totalRowCount, batch.Arena);
                    batch.Add(values);
                    if (batch.IsFull) { yield return batch; batch = null; }
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void FillIndexRow(
        DataValue[] cells, string tableName, string columnName, string indexType,
        long? entryCount, int chunkCount, long totalRowCount, Arena arena)
    {
        cells[0] = DataValue.FromString(tableName, arena);
        cells[1] = DataValue.FromString(columnName, arena);
        cells[2] = DataValue.FromString(indexType, arena);
        cells[3] = entryCount.HasValue ? DataValue.FromInt64(entryCount.Value) : DataValue.Null(DataKind.Int64);
        cells[4] = DataValue.FromInt32(chunkCount);
        cells[5] = DataValue.FromInt64(totalRowCount);
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("table_name",     DataKind.String, nullable: false),
        new ColumnInfo("column_name",    DataKind.String, nullable: false),
        new ColumnInfo("index_type",     DataKind.String, nullable: false),
        new ColumnInfo("entry_count",    DataKind.Int64,  nullable: true),
        new ColumnInfo("chunk_count",    DataKind.Int32,  nullable: false),
        new ColumnInfo("total_row_count",DataKind.Int64,  nullable: false),
    ]);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Virtual table listing pairwise column interaction statistics for all tables
/// that have computed them.
/// Query with <c>SELECT * FROM datum_catalog.interactions</c>.
/// </summary>
/// <remarks>
/// Rows are sourced from <see cref="QueryResultsManifest.Interactions"/>.
/// Tables without a manifest or without computed interactions produce no rows.
/// </remarks>
internal sealed class DatumCatalogInteractionsProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.interactions";

    private static readonly Schema _schema = BuildSchema();

    private readonly TableCatalog _catalog;

    /// <summary>Creates a provider that reflects interaction data from all registered tables.</summary>
    internal DatumCatalogInteractionsProvider(Pool pool, TableCatalog catalog) : base(pool, TableName)
    {
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 0;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        ITableProvider[] snapshot = [.. _catalog];

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (ITableProvider provider in snapshot)
        {
            QueryResultsManifest? manifest = provider.GetManifest();
            if (manifest?.Interactions is null)
            {
                continue;
            }

            foreach (ColumnInteraction interaction in manifest.Interactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);
                DataValue[] values = Pool.RentDataValues(_schema.Columns.Count);
                FillInteractionRow(values, provider.Name, interaction, batch.Arena);
                batch.Add(values);
                if (batch.IsFull) { yield return batch; batch = null; }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static void FillInteractionRow(
        DataValue[] cells, string tableName, ColumnInteraction interaction, Arena arena)
    {
        cells[0]  = DataValue.FromString(tableName, arena);
        cells[1]  = DataValue.FromString(interaction.ColumnA, arena);
        cells[2]  = DataValue.FromString(interaction.ColumnB, arena);
        cells[3]  = NullableFloat64(interaction.Pearson);
        cells[4]  = NullableFloat64(interaction.Spearman);
        cells[5]  = NullableFloat64(interaction.CramerV);
        cells[6]  = NullableFloat64(interaction.AnovaFStatistic);
        cells[7]  = NullableFloat64(interaction.MutualInformation);
        cells[8]  = NullableFloat64(interaction.TheilUAB);
        cells[9]  = NullableFloat64(interaction.TheilUBA);
        cells[10] = NullableFloat64(interaction.MissingnessCorrelation);
    }

    private static DataValue NullableFloat64(double? value) =>
        value.HasValue ? DataValue.FromFloat64(value.Value) : DataValue.Null(DataKind.Float64);

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("table_name",              DataKind.String,  nullable: false),
        new ColumnInfo("column_a",                DataKind.String,  nullable: false),
        new ColumnInfo("column_b",                DataKind.String,  nullable: false),
        new ColumnInfo("pearson",                 DataKind.Float64, nullable: true),
        new ColumnInfo("spearman",                DataKind.Float64, nullable: true),
        new ColumnInfo("cramer_v",                DataKind.Float64, nullable: true),
        new ColumnInfo("anova_f",                 DataKind.Float64, nullable: true),
        new ColumnInfo("mutual_information",      DataKind.Float64, nullable: true),
        new ColumnInfo("theil_u_ab",              DataKind.Float64, nullable: true),
        new ColumnInfo("theil_u_ba",              DataKind.Float64, nullable: true),
        new ColumnInfo("missingness_correlation", DataKind.Float64, nullable: true),
    ]);
}
