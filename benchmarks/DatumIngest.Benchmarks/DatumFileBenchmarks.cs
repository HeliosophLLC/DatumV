using BenchmarkDotNet.Attributes;
using DatumIngest.DatumFile;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Output.Writers;
using DatumIngest.Statistics;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks comparing the plain <c>.datum</c> write path against the fused pipeline
/// (simultaneous index and statistics accumulation), and measuring parallel column decode
/// throughput via <see cref="DatumMemoryMappedReader"/>.
/// </summary>
[MemoryDiagnoser]
public class DatumFileBenchmarks
{
    private Row[] _rows = null!;
    private Schema _schema = null!;
    private string _tempDirectory = null!;
    private string _preWrittenPath = null!;

    /// <summary>Rows per run. Kept at 50K to exercise multi-row-group behaviour without OOM.</summary>
    [Params(50_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _rows = SyntheticDataGenerator.GenerateRows(RowCount);
        _schema = new Schema(new ColumnInfo[]
        {
            new("id", DataKind.Float32, false),
            new("name", DataKind.String, false),
            new("value", DataKind.Float32, false),
            new("category", DataKind.String, false),
            new("score", DataKind.Float32, false)
        });
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum_bench_datumfile");
        Directory.CreateDirectory(_tempDirectory);

        // Pre-write a datum file used by the read benchmark.
        _preWrittenPath = Path.Combine(_tempDirectory, "prewritten.datum");
        WriteRowsToFile(_preWrittenPath, _rows, _schema).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Baseline: write rows to a <c>.datum</c> file with no index or statistics accumulation.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Write datum (no index/stats)")]
    public async Task WriteDatumBaseline()
    {
        string path = Path.Combine(_tempDirectory, $"baseline_{Guid.NewGuid():N}.datum");
        await WriteRowsToFile(path, _rows, _schema).ConfigureAwait(false);
    }

    /// <summary>
    /// Parallel column decode: reads all five columns from every row group using
    /// <see cref="DatumMemoryMappedReader.ReadColumnsParallel"/>.
    /// </summary>
    [Benchmark(Description = "Read columns parallel (MMF)")]
    public void ReadColumnsParallel()
    {
        int[] allColumns = [0, 1, 2, 3, 4];
        using DatumMemoryMappedReader reader = DatumMemoryMappedReader.Open(_preWrittenPath);
        for (int rowGroup = 0; rowGroup < reader.RowGroupCount; rowGroup++)
        {
            _ = reader.ReadColumnsParallel(rowGroup, allColumns);
        }
    }

    // ──────────────────── Helpers ────────────────────

    private static async Task WriteRowsToFile(string path, Row[] rows, Schema schema)
    {
        DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema).ConfigureAwait(false);
        foreach (Row row in rows)
        {
            await writer.WriteRowAsync(row).ConfigureAwait(false);
        }
        await writer.FinalizeAsync().ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
