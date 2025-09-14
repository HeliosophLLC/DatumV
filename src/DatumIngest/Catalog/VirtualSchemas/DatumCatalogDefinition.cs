using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Catalog.VirtualSchemas;

/// <summary>
/// The <c>datum_catalog</c> virtual schema, providing DatumIngest-specific metadata views
/// for providers, functions, and per-column statistics.
/// </summary>
internal sealed class DatumCatalogDefinition : IVirtualSchema
{
    private static readonly Dictionary<string, IVirtualTableSource> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["providers"] = new ProvidersSource(),
        ["functions"] = new FunctionsSource(),
        ["statistics"] = new StatisticsSource(),
    };

    /// <inheritdoc />
    public string Name => "datum_catalog";

    /// <inheritdoc />
    public IReadOnlyList<string> TableNames { get; } = ["providers", "functions", "statistics"];

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
    /// Lists all registered functions with their type classification
    /// (<c>SCALAR</c>, <c>AGGREGATE</c>, <c>TABLE_VALUED</c>, or <c>WINDOW</c>).
    /// </summary>
    private sealed class FunctionsSource : IVirtualTableSource
    {
        private static readonly Schema OutputSchema = new(
        [
            new ColumnInfo("function_name", DataKind.String, false),
            new ColumnInfo("function_type", DataKind.String, false),
        ]);

        private static readonly string[] ColumnNames = ["function_name", "function_type"];

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
            DataValue[] values =
            [
                DataValue.FromString(functionName),
                DataValue.FromString(functionType),
            ];

            batch.Add(new Row(ColumnNames, values));
        }
    }

    // ─────────────────── datum_catalog.statistics ───────────────────

    /// <summary>
    /// Lists per-column statistics from manifests for all tables that have them.
    /// Includes row count, distinct count, null ratio, and min/max values (where available).
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
        ]);

        private static readonly string[] ColumnNames =
            ["table_name", "column_name", "data_type", "row_count", "distinct_count", "null_ratio", "min_value", "max_value"];

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

                    DataValue[] values =
                    [
                        DataValue.FromString(tableName),
                        DataValue.FromString(feature.Name),
                        DataValue.FromString(feature.Kind.ToString()),
                        DataValue.FromInt64(manifest.RowCount),
                        DataValue.FromInt64(feature.EstimatedDistinctCount),
                        feature.NullRatio.HasValue ? DataValue.FromFloat64(feature.NullRatio.Value) : DataValue.Null(DataKind.Float64),
                        minValue is not null ? DataValue.FromString(minValue) : DataValue.Null(DataKind.String),
                        maxValue is not null ? DataValue.FromString(maxValue) : DataValue.Null(DataKind.String),
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
}
