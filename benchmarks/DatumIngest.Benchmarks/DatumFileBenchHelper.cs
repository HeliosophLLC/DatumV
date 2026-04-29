using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Which table-provider backing a benchmark uses for its source rows.
/// Exposed as a BenchmarkDotNet <c>[Params]</c> axis so the report renders
/// the same query against both providers side-by-side.
/// </summary>
public enum ProviderKind
{
    /// <summary>
    /// <see cref="DatumIngest.Catalog.Providers.InMemoryTableProvider"/> over pre-materialized
    /// <see cref="Row"/>s. Isolates operator perf — no file I/O, no real binary decode.
    /// </summary>
    InMemory,

    /// <summary>
    /// <see cref="DatumIngest.Catalog.Providers.DatumFileTableProviderV2"/> reading a real
    /// <c>.datum</c> file written once in <c>[GlobalSetup]</c>. Measures the engine
    /// end-to-end — closer to what users actually pay and what cross-engine
    /// comparisons (DuckDB, SQLite) measure.
    /// </summary>
    DatumFile,
}

/// <summary>
/// Utilities for persisting benchmark fixture rows as <c>.datum</c> files so the same
/// row set can drive both the <see cref="ProviderKind.InMemory"/> and
/// <see cref="ProviderKind.DatumFile"/> paths from a single <c>[GlobalSetup]</c>.
/// </summary>
internal static class DatumFileBenchHelper
{
    private const int WriteBatchSize = 1024;

    /// <summary>
    /// Writes <paramref name="rows"/> to a fresh <c>.datum</c> file at
    /// <paramref name="path"/>, framed by the supplied <paramref name="columns"/> schema.
    /// </summary>
    /// <remarks>
    /// Each row is copied cell-by-cell from its <see cref="Row"/> into a pool-rented
    /// staging batch. The benchmark fixtures only use inline-encodable DataValues
    /// (short strings, Float32/Int64), so no cross-arena stabilisation is required.
    /// If a future fixture introduces arena-backed payloads, the copy loop should
    /// switch to <c>DataValueRetention.Stabilize</c>.
    /// </remarks>
    public static string WriteRowsToDatumFile(
        Pool pool, string path, Row[] rows, ColumnDescriptorV2[] columns)
    {
        string[] names = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++) names[i] = columns[i].Name;
        ColumnLookup lookup = new(names);
        Arena arena = new();

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize(columns);

        int rowIndex = 0;
        while (rowIndex < rows.Length)
        {
            int batchEnd = Math.Min(rowIndex + WriteBatchSize, rows.Length);
            int count = batchEnd - rowIndex;
            RowBatch batch = pool.RentRowBatch(lookup, capacity: count, arena: arena);

            for (int rowOffset = 0; rowOffset < count; rowOffset++, rowIndex++)
            {
                DataValue[] values = pool.RentDataValues(columns.Length);
                for (int c = 0; c < columns.Length; c++)
                {
                    values[c] = rows[rowIndex][c];
                }
                batch.Add(values);
            }

            writer.WriteRowBatch(batch);
        }

        writer.FinalizeWriter();
        return path;
    }

    /// <summary>
    /// Writes raw boxed-CLR rows (e.g. <c>[42f, "alpha", 3.14f]</c>) to a fresh
    /// <c>.datum</c> file. Provided for benchmark classes whose fixture builders
    /// produce <c>object?[][]</c> directly instead of <see cref="Row"/>[].
    /// </summary>
    public static string WriteRawRowsToDatumFile(
        Pool pool, string path, object?[][] rawRows, ColumnDescriptorV2[] columns)
    {
        string[] names = new string[columns.Length];
        for (int i = 0; i < columns.Length; i++) names[i] = columns[i].Name;
        ColumnLookup lookup = new(names);
        Arena arena = new();

        using DatumFileWriterV2 writer = new(path, sidecarPath: null);
        writer.Initialize(columns);

        int rowIndex = 0;
        while (rowIndex < rawRows.Length)
        {
            int batchEnd = Math.Min(rowIndex + WriteBatchSize, rawRows.Length);
            int count = batchEnd - rowIndex;
            RowBatch batch = pool.RentRowBatch(lookup, capacity: count, arena: arena);

            for (int rowOffset = 0; rowOffset < count; rowOffset++, rowIndex++)
            {
                DataValue[] values = pool.RentDataValues(columns.Length);
                object?[] rawRow = rawRows[rowIndex];
                for (int c = 0; c < columns.Length; c++)
                {
                    values[c] = ConvertRawCell(rawRow[c], columns[c], arena);
                }
                batch.Add(values);
            }

            writer.WriteRowBatch(batch);
        }

        writer.FinalizeWriter();
        return path;
    }

    private static DataValue ConvertRawCell(object? raw, ColumnDescriptorV2 column, Arena arena)
    {
        if (raw is null)
        {
            return DataValue.Null(column.Kind);
        }

        return column.Kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(raw switch
            {
                float f => f,
                int i => (float)i,
                double d => (float)d,
                _ => Convert.ToSingle(raw),
            }),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(raw)),
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(raw)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(raw)),
            DataKind.String => DataValue.FromString((string)raw, arena),
            DataKind.Boolean => DataValue.FromBoolean((bool)raw),
            _ => throw new NotSupportedException(
                $"DatumFileBenchHelper.ConvertRawCell does not handle {column.Kind}. "
                + "Extend the switch when adding a benchmark that needs it."),
        };
    }
}
