using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog.VirtualSchemas;

/// <summary>
/// The <c>datum_catalog</c> virtual schema, providing DatumIngest-specific metadata views
/// for providers, functions, per-column statistics, function parameters, indexes,
/// and column interactions.
/// </summary>
internal sealed class DatumCatalogDefinition : IVirtualSchema
{
    private static readonly Dictionary<string, IVirtualTableSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["providers"] = new ProvidersSource(),
        ["functions"] = new FunctionsSource(),
        ["function_parameters"] = new FunctionParametersSource(),
        ["statistics"] = new StatisticsSource(),
        ["indexes"] = new IndexesSource(),
        ["interactions"] = new InteractionsSource(),
    };

    /// <inheritdoc />
    public string Name => "datum_catalog";

    /// <inheritdoc />
    public IReadOnlyList<string> TableNames { get; } =
        ["providers", "functions", "function_parameters", "statistics", "indexes", "interactions"];

    /// <inheritdoc />
    public IVirtualTableSource? TryResolve(string tableName)
    {
        Sources.TryGetValue(tableName, out IVirtualTableSource? source);
        return source;
    }

    // ─────────────────── datum_catalog.providers ───────────────────

    /// <summary>
    /// Lists all registered format providers (e.g. csv, parquet, hdf5).
    /// </summary>
    private sealed class ProvidersSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("provider_name", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames = ["provider_name"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(16);

            foreach (string providerName in context.Catalog.ProviderNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = [DataValue.FromString(providerName)];
                batch.Add(new Row(ColumnNames, values));

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = RowBatch.Rent(16);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    // ─────────────────── datum_catalog.functions ───────────────────

    /// <summary>
    /// Lists all registered functions with their type classification, category,
    /// return type, description, parameter count, and query-unit cost.
    /// </summary>
    private sealed class FunctionsSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("function_name", DataKind.String, false),
            new ColumnInfo("function_type", DataKind.String, false),
            new ColumnInfo("category", DataKind.String, true),
            new ColumnInfo("return_type", DataKind.String, true),
            new ColumnInfo("description", DataKind.String, true),
            new ColumnInfo("parameter_count", DataKind.Int32, true),
            new ColumnInfo("query_unit_cost", DataKind.Int32, true),
        ]);

        private static readonly string[] ColumnNames =
            ["function_name", "function_type", "category", "return_type", "description", "parameter_count", "query_unit_cost"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string name in context.FunctionRegistry.ScalarFunctionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddRow(batch, name, "SCALAR");
                if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
            }

            foreach (string name in context.FunctionRegistry.AggregateFunctionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddRow(batch, name, "AGGREGATE");
                if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
            }

            foreach (string name in context.FunctionRegistry.TableValuedFunctionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddRow(batch, name, "TABLE_VALUED");
                if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
            }

            foreach (string name in context.FunctionRegistry.WindowFunctionNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddRow(batch, name, "WINDOW");
                if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static void AddRow(RowBatch batch, string functionName, string functionType)
        {
            FunctionSignature? signature = FunctionDocumentation.TryGet(functionName);

            DataValue[] values =
            [
                DataValue.FromString(functionName),
                DataValue.FromString(functionType),
                signature is not null ? DataValue.FromString(signature.Category.ToString()) : DataValue.Null(DataKind.String),
                signature?.ReturnType is not null ? DataValue.FromString(signature.ReturnType) : DataValue.Null(DataKind.String),
                signature?.Description is not null ? DataValue.FromString(signature.Description) : DataValue.Null(DataKind.String),
                signature is not null ? DataValue.FromInt32(signature.Parameters.Count) : DataValue.Null(DataKind.Int32),
                signature is not null ? DataValue.FromInt32(signature.QueryUnitCost) : DataValue.Null(DataKind.Int32),
            ];

            batch.Add(new Row(ColumnNames, values));
        }
    }

    // ─────────────────── datum_catalog.function_parameters ───────────────────

    /// <summary>
    /// Lists all documented function parameters with ordinal position, name,
    /// data type, and optionality.
    /// </summary>
    private sealed class FunctionParametersSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("function_name", DataKind.String, false),
            new ColumnInfo("ordinal_position", DataKind.Int32, false),
            new ColumnInfo("parameter_name", DataKind.String, false),
            new ColumnInfo("data_type", DataKind.String, false),
            new ColumnInfo("is_optional", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames =
            ["function_name", "ordinal_position", "parameter_name", "data_type", "is_optional"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (FunctionSignature signature in FunctionDocumentation.All)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int ordinal = 0; ordinal < signature.Parameters.Count; ordinal++)
                {
                    ParameterSignature parameter = signature.Parameters[ordinal];

                    DataValue[] values =
                    [
                        DataValue.FromString(signature.Name),
                        DataValue.FromInt32(ordinal + 1),
                        DataValue.FromString(parameter.Name),
                        DataValue.FromString(parameter.Kind),
                        DataValue.FromString(parameter.IsOptional ? "YES" : "NO"),
                    ];

                    batch.Add(new Row(ColumnNames, values));

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = RowBatch.Rent(64);
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    // ─────────────────── datum_catalog.statistics ───────────────────

    /// <summary>
    /// Lists per-column statistics from manifests for all tables that have them.
    /// Includes row count, distinct count, null ratio, min/max values, entropy,
    /// distribution shape metrics, quantiles, and type-specific features.
    /// </summary>
    private sealed class StatisticsSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("table_name", DataKind.String, false),
            new ColumnInfo("column_name", DataKind.String, false),
            new ColumnInfo("data_type", DataKind.String, false),
            new ColumnInfo("row_count", DataKind.Int64, false),
            new ColumnInfo("distinct_count", DataKind.Int64, false),
            new ColumnInfo("null_ratio", DataKind.Float64, true),
            new ColumnInfo("min_value", DataKind.String, true),
            new ColumnInfo("max_value", DataKind.String, true),
            new ColumnInfo("entropy", DataKind.Float64, true),
            new ColumnInfo("dominant_value_ratio", DataKind.Float64, true),
            new ColumnInfo("is_constant", DataKind.String, true),
            new ColumnInfo("column_role", DataKind.String, true),
            new ColumnInfo("top_value", DataKind.String, true),
            new ColumnInfo("top_value_frequency", DataKind.Int64, true),
            new ColumnInfo("mean", DataKind.Float64, true),
            new ColumnInfo("standard_deviation", DataKind.Float64, true),
            new ColumnInfo("skewness", DataKind.Float64, true),
            new ColumnInfo("kurtosis", DataKind.Float64, true),
            new ColumnInfo("p25", DataKind.Float64, true),
            new ColumnInfo("p50", DataKind.Float64, true),
            new ColumnInfo("p75", DataKind.Float64, true),
            new ColumnInfo("zero_ratio", DataKind.Float64, true),
            new ColumnInfo("outlier_ratio", DataKind.Float64, true),
            new ColumnInfo("integer_valued", DataKind.String, true),
            new ColumnInfo("min_length", DataKind.Int32, true),
            new ColumnInfo("max_length", DataKind.Int32, true),
            new ColumnInfo("true_ratio", DataKind.Float64, true),
        ]);

        private static readonly string[] ColumnNames =
        [
            "table_name", "column_name", "data_type", "row_count", "distinct_count",
            "null_ratio", "min_value", "max_value", "entropy", "dominant_value_ratio",
            "is_constant", "column_role", "top_value", "top_value_frequency",
            "mean", "standard_deviation", "skewness", "kurtosis",
            "p25", "p50", "p75", "zero_ratio", "outlier_ratio", "integer_valued",
            "min_length", "max_length", "true_ratio",
        ];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string tableName in context.Catalog.TableNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!context.Catalog.TryGetManifest(tableName, out QueryResultsManifest? manifest) || manifest is null)
                {
                    continue;
                }

                foreach (FeatureManifest feature in manifest.Features)
                {
                    (string? minValue, string? maxValue) = ExtractMinMax(feature);
                    DataValue[] values = BuildStatisticsRow(tableName, feature, manifest.RowCount, minValue, maxValue);

                    batch.Add(new Row(ColumnNames, values));

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = RowBatch.Rent(64);
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static DataValue[] BuildStatisticsRow(
            string tableName,
            FeatureManifest feature,
            long rowCount,
            string? minValue,
            string? maxValue)
        {
            // Extract top-K leader (most frequent value and its count).
            string? topValue = feature.TopKValues.Count > 0 ? feature.TopKValues[0].Value : null;
            long? topValueFrequency = feature.TopKValues.Count > 0 ? feature.TopKValues[0].Frequency : null;

            // Extract numeric-specific fields.
            NumericFeatureManifest? numeric = feature as NumericFeatureManifest;
            QuantileData? quantiles = numeric?.Quantiles;

            // Extract string-specific fields.
            StringFeatureManifest? text = feature as StringFeatureManifest;

            // Extract boolean-specific fields.
            BooleanFeatureManifest? boolean = feature as BooleanFeatureManifest;

            return
            [
                DataValue.FromString(tableName),
                DataValue.FromString(feature.Name),
                DataValue.FromString(feature.Kind.ToString()),
                DataValue.FromInt64(rowCount),
                DataValue.FromInt64(feature.EstimatedDistinctCount),
                NullableFloat64(feature.NullRatio),
                NullableString(minValue),
                NullableString(maxValue),
                NullableFloat64(feature.Entropy),
                NullableFloat64(feature.DominantValueRatio),
                DataValue.FromString(feature.IsConstant ? "YES" : "NO"),
                feature.Role.HasValue ? DataValue.FromString(feature.Role.Value.ToString()) : DataValue.Null(DataKind.String),
                NullableString(topValue),
                topValueFrequency.HasValue ? DataValue.FromInt64(topValueFrequency.Value) : DataValue.Null(DataKind.Int64),
                numeric is not null ? DataValue.FromFloat64(numeric.Mean) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromFloat64(numeric.StandardDeviation) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromFloat64(numeric.Skewness) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromFloat64(numeric.Kurtosis) : DataValue.Null(DataKind.Float64),
                quantiles is not null ? DataValue.FromFloat64(quantiles.P25) : DataValue.Null(DataKind.Float64),
                quantiles is not null ? DataValue.FromFloat64(quantiles.P50) : DataValue.Null(DataKind.Float64),
                quantiles is not null ? DataValue.FromFloat64(quantiles.P75) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromFloat64(numeric.ZeroRatio) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromFloat64(numeric.OutlierRatio) : DataValue.Null(DataKind.Float64),
                numeric is not null ? DataValue.FromString(numeric.IntegerValued ? "YES" : "NO") : DataValue.Null(DataKind.String),
                text is not null ? DataValue.FromInt32(text.MinLength) : DataValue.Null(DataKind.Int32),
                text is not null ? DataValue.FromInt32(text.MaxLength) : DataValue.Null(DataKind.Int32),
                boolean is not null ? DataValue.FromFloat64(boolean.TrueRatio) : DataValue.Null(DataKind.Float64),
            ];
        }

        private static DataValue NullableFloat64(double? value) =>
            value.HasValue ? DataValue.FromFloat64(value.Value) : DataValue.Null(DataKind.Float64);

        private static DataValue NullableString(string? value) =>
            value is not null ? DataValue.FromString(value) : DataValue.Null(DataKind.String);

        /// <summary>
        /// Extracts min/max string representations from kind-specific manifest subclasses.
        /// </summary>
        private static (string? Min, string? Max) ExtractMinMax(FeatureManifest feature)
        {
            return feature switch
            {
                NumericFeatureManifest numeric => (numeric.Min.ToString("G"), numeric.Max.ToString("G")),
                StringFeatureManifest text => (text.MinLength.ToString(), text.MaxLength.ToString()),
                TemporalFeatureManifest temporal => (temporal.Earliest, temporal.Latest),
                _ => (null, null),
            };
        }
    }

    // ─────────────────── datum_catalog.indexes ───────────────────

    /// <summary>
    /// Lists per-column index metadata for all tables that have source indexes.
    /// Reports each (table, column, index_type) combination with entry count and chunk info.
    /// </summary>
    private sealed class IndexesSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("table_name", DataKind.String, false),
            new ColumnInfo("column_name", DataKind.String, false),
            new ColumnInfo("index_type", DataKind.String, false),
            new ColumnInfo("entry_count", DataKind.Int64, true),
            new ColumnInfo("chunk_count", DataKind.Int32, false),
            new ColumnInfo("total_row_count", DataKind.Int64, false),
        ]);

        private static readonly string[] ColumnNames =
            ["table_name", "column_name", "index_type", "entry_count", "chunk_count", "total_row_count"];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string tableName in context.Catalog.TableNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!context.Catalog.TryGetIndex(tableName, out SourceIndex? sourceIndex) || sourceIndex is null)
                {
                    continue;
                }

                int chunkCount = sourceIndex.Chunks.Count;
                long totalRowCount = sourceIndex.Schema.TotalRowCount;

                // Sorted indexes.
                if (sourceIndex.SortedIndexes is not null)
                {
                    foreach (string columnName in sourceIndex.SortedIndexes.ColumnNames)
                    {
                        long? entryCount = sourceIndex.SortedIndexes.TryGetIndex(columnName, out SortedValueIndex? sortedIndex)
                            ? sortedIndex.EntryCount
                            : null;

                        AddRow(batch, tableName, columnName, "SORTED", entryCount, chunkCount, totalRowCount);
                        if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
                    }
                }

                // B+Tree indexes.
                if (sourceIndex.BPlusTreeIndexes is not null)
                {
                    foreach (string columnName in sourceIndex.BPlusTreeIndexes.ColumnNames)
                    {
                        long? entryCount = sourceIndex.BPlusTreeIndexes.TryGetIndex(columnName, out BPlusTreeColumnIndex? btreeIndex)
                            ? btreeIndex.EntryCount
                            : null;

                        AddRow(batch, tableName, columnName, "BTREE", entryCount, chunkCount, totalRowCount);
                        if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
                    }
                }

                // Bitmap indexes.
                if (sourceIndex.BitmapIndexes is not null)
                {
                    foreach (string columnName in sourceIndex.BitmapIndexes.ColumnNames)
                    {
                        AddRow(batch, tableName, columnName, "BITMAP", null, chunkCount, totalRowCount);
                        if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
                    }
                }

                // Bloom filters.
                if (sourceIndex.BloomFilters is not null)
                {
                    foreach (string columnName in sourceIndex.BloomFilters.ColumnNames)
                    {
                        AddRow(batch, tableName, columnName, "BLOOM", null, chunkCount, totalRowCount);
                        if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
                    }
                }

                // Memory-mapped sorted indexes.
                if (sourceIndex.MappedSortedIndexes is not null)
                {
                    foreach ((string columnName, MappedSortedIndex mappedIndex) in sourceIndex.MappedSortedIndexes)
                    {
                        AddRow(batch, tableName, columnName, "MAPPED_SORTED", mappedIndex.EntryCount, chunkCount, totalRowCount);
                        if (batch.IsFull) { yield return batch; batch = RowBatch.Rent(64); }
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static void AddRow(
            RowBatch batch,
            string tableName,
            string columnName,
            string indexType,
            long? entryCount,
            int chunkCount,
            long totalRowCount)
        {
            DataValue[] values =
            [
                DataValue.FromString(tableName),
                DataValue.FromString(columnName),
                DataValue.FromString(indexType),
                entryCount.HasValue ? DataValue.FromInt64(entryCount.Value) : DataValue.Null(DataKind.Int64),
                DataValue.FromInt32(chunkCount),
                DataValue.FromInt64(totalRowCount),
            ];

            batch.Add(new Row(ColumnNames, values));
        }
    }

    // ─────────────────── datum_catalog.interactions ───────────────────

    /// <summary>
    /// Lists pairwise column interaction statistics (correlation coefficients,
    /// mutual information, etc.) for all tables that have computed interactions.
    /// </summary>
    private sealed class InteractionsSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("table_name", DataKind.String, false),
            new ColumnInfo("column_a", DataKind.String, false),
            new ColumnInfo("column_b", DataKind.String, false),
            new ColumnInfo("pearson", DataKind.Float64, true),
            new ColumnInfo("spearman", DataKind.Float64, true),
            new ColumnInfo("cramer_v", DataKind.Float64, true),
            new ColumnInfo("anova_f", DataKind.Float64, true),
            new ColumnInfo("mutual_information", DataKind.Float64, true),
            new ColumnInfo("theil_u_ab", DataKind.Float64, true),
            new ColumnInfo("theil_u_ba", DataKind.Float64, true),
            new ColumnInfo("missingness_correlation", DataKind.Float64, true),
        ]);

        private static readonly string[] ColumnNames =
        [
            "table_name", "column_a", "column_b", "pearson", "spearman",
            "cramer_v", "anova_f", "mutual_information", "theil_u_ab", "theil_u_ba",
            "missingness_correlation",
        ];

        public Schema GetSchema() => OutputSchema;

        public async IAsyncEnumerable<RowBatch> ScanAsync(
            VirtualTableContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (string tableName in context.Catalog.TableNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!context.Catalog.TryGetManifest(tableName, out QueryResultsManifest? manifest) ||
                    manifest?.Interactions is null)
                {
                    continue;
                }

                foreach (ColumnInteraction interaction in manifest.Interactions)
                {
                    DataValue[] values =
                    [
                        DataValue.FromString(tableName),
                        DataValue.FromString(interaction.ColumnA),
                        DataValue.FromString(interaction.ColumnB),
                        NullableFloat64(interaction.Pearson),
                        NullableFloat64(interaction.Spearman),
                        NullableFloat64(interaction.CramerV),
                        NullableFloat64(interaction.AnovaFStatistic),
                        NullableFloat64(interaction.MutualInformation),
                        NullableFloat64(interaction.TheilUAB),
                        NullableFloat64(interaction.TheilUBA),
                        NullableFloat64(interaction.MissingnessCorrelation),
                    ];

                    batch.Add(new Row(ColumnNames, values));

                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = RowBatch.Rent(64);
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
            else
            {
                batch.Return();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static DataValue NullableFloat64(double? value) =>
            value.HasValue ? DataValue.FromFloat64(value.Value) : DataValue.Null(DataKind.Float64);
    }
}
