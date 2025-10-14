using DatumIngest.Pooling;
using DatumIngest.Model;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Parquet;
using System.Text;

namespace DatumIngest.Tests.Serialization;

/// <summary>
/// Verifies that deserializer hot paths produce near-zero allocations for numeric
/// columns. Uses <see cref="GC.GetAllocatedBytesForCurrentThread"/> to measure
/// per-thread allocations precisely. Must run in Release (no POOL_DIAGNOSTICS).
/// </summary>
public sealed class DeserializerAllocationTests
{
    private static readonly string FixturesPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    private static SerializationContext CreateContext()
    {
        PoolBacking backing = new();
        Pool pool = new(backing);
        return new SerializationContext(pool);
    }

    /// <summary>
    /// Measures allocations during batch consumption only — not setup, not teardown.
    /// The <paramref name="perRow"/> action is called on each row inside the measurement window.
    /// </summary>
    private static async Task<(long Allocated, int RowCount)> MeasureBatchConsumptionAsync(
        IFormatDeserializer deserializer,
        SerializationContext context,
        Action<Row>? perRow = null)
    {
        long totalAllocated = 0;
        int totalRows = 0;

        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            // Measure only the per-row access within each batch.
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < batch.Count; i++)
            {
                perRow?.Invoke(batch[i]);
            }

            totalAllocated += GC.GetAllocatedBytesForCurrentThread() - before;
            totalRows += batch.Count;

            context.Pool.ReturnRowBatch(batch, returnDataValues: true);
        }

        return (totalAllocated, totalRows);
    }

    // ───────────────────────── CSV numeric hot path ─────────────────────────

    [Fact]
    public async Task CsvDeserializer_NumericRowAccess_NearZeroAllocations()
    {
        StringBuilder sb = new();
        sb.AppendLine("a,b,c");
        for (int i = 0; i < 10_000; i++)
            sb.AppendLine($"{i},{i * 1.5},{i % 2}");

        string csvContent = sb.ToString();

        using var context = CreateContext();
        var descriptor = new MemoryDescriptor(csvContent, "measure.csv");
        var csv = new CsvDeserializer(descriptor);

        var (allocated, rowCount) = await MeasureBatchConsumptionAsync(csv, context, row =>
        {
            // Force reads to ensure values are materialized.
            _ = row["a"].AsInt32();
            _ = row["b"].AsFloat64();
            _ = row["c"].AsBoolean();
        });

        // Numeric field access should allocate nothing — values are inline in DataValue.
        double bytesPerRow = rowCount > 0 ? allocated / (double)rowCount : 0;
        Assert.True(bytesPerRow < 1.0,
            $"CSV: {bytesPerRow:F2} bytes/row ({allocated:N0} bytes / {rowCount:N0} rows).");
    }

    // ───────────────────────── JSONL numeric hot path ─────────────────────────

    [Fact]
    public async Task JsonlDeserializer_NumericRowAccess_NearZeroAllocations()
    {
        StringBuilder sb = new();
        for (int i = 0; i < 10_000; i++)
            sb.AppendLine($"{{\"a\":{i},\"b\":{i * 1.5},\"c\":{(i % 2 == 0).ToString().ToLower()}}}");

        string jsonlContent = sb.ToString();

        using var context = CreateContext();
        var descriptor = new MemoryDescriptor(jsonlContent, "measure.jsonl");
        var jsonl = new DatumIngest.Serialization.Jsonl.JsonlDeserializer(descriptor);

        var (allocated, rowCount) = await MeasureBatchConsumptionAsync(jsonl, context, row =>
        {
            _ = row["a"].AsInt64();
            _ = row["b"].AsFloat64();
            _ = row["c"].AsBoolean();
        });

        double bytesPerRow = rowCount > 0 ? allocated / (double)rowCount : 0;
        Assert.True(bytesPerRow < 1.0,
            $"JSONL: {bytesPerRow:F2} bytes/row ({allocated:N0} bytes / {rowCount:N0} rows).");
    }

    // ───────────────────────── Parquet numeric hot path ─────────────────────────

    [Fact]
    public async Task ParquetDeserializer_NumericRowAccess_NearZeroAllocations()
    {
        string parquetPath = Path.Combine(FixturesPath, "green_tripdata_2026-01.parquet");

        if (!File.Exists(parquetPath))
            return;

        using var context = CreateContext();
        var parquet = new ParquetDeserializer(new FileFormatDescriptor(parquetPath));

        // Parquet has string columns — only measure numeric column access.
        var (allocated, rowCount) = await MeasureBatchConsumptionAsync(parquet, context, row =>
        {
            // Access only numeric columns (no string materialization).
            if (row.TryGetValue("trip_distance", out DataValue dist) && !dist.IsNull)
                _ = dist.AsFloat64();
            if (row.TryGetValue("fare_amount", out DataValue fare) && !fare.IsNull)
                _ = fare.AsFloat64();
            if (row.TryGetValue("passenger_count", out DataValue pax) && !pax.IsNull)
                _ = pax.AsInt64();
        });

        // Numeric reads from typed arrays should be zero-allocation.
        double bytesPerRow = rowCount > 0 ? allocated / (double)rowCount : 0;
        Assert.True(bytesPerRow < 5.0,
            $"Parquet: {bytesPerRow:F2} bytes/row ({allocated:N0} bytes / {rowCount:N0} rows).");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private sealed class MemoryDescriptor : FileFormatDescriptor
    {
        private readonly string _content;

        public MemoryDescriptor(string content, string fileName)
            : base(fileName)
        {
            _content = content;
        }

        public override Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_content);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
    }
}
