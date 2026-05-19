using System.Diagnostics;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;

string sourcePath = args.Length > 0
    ? args[0]
    : @"E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.datum";

string destPath = args.Length > 1
    ? args[1]
    : Path.ChangeExtension(sourcePath, ".datum-index");

// Optional: third CLI arg = max wall-clock seconds before cancelling.
// Useful for profiling large sources without committing to a full index build.
int? timeoutSeconds = args.Length > 2 && int.TryParse(args[2], out int t) ? t : null;

// Optional: fourth CLI arg = memory preset. "low" → MultiTenantServer (smaller
// chunks, lower peak working set). Anything else → Default (max throughput).
IndexOptions indexOptions = args.Length > 3
    && args[3].Equals("low", StringComparison.OrdinalIgnoreCase)
    ? IndexOptions.MultiTenantServer
    : IndexOptions.Default;

// No downstream path currently consumes ChunkColumnStatistics.EstimatedCardinality for
// planning. Skipping the HLL updates shaves ~20s off a Chicago-scale build.
indexOptions = indexOptions with { ComputeCardinality = false };

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source file not found: {sourcePath}");
    return 1;
}

long sourceSize = new FileInfo(sourcePath).Length;
Console.WriteLine($"Source:      {sourcePath}");
Console.WriteLine($"Dest:        {destPath}");
Console.WriteLine($"Source size: {sourceSize:N0} bytes ({sourceSize / (1024.0 * 1024.0):F1} MB)");
if (timeoutSeconds is int sec) Console.WriteLine($"Timeout:     {sec}s");
Console.WriteLine($"Chunk size:  {indexOptions.ChunkSize:N0} rows");
Console.WriteLine();

PoolBacking backing = new();
Pool pool = new(backing);
Indexer indexer = new(pool);

DatumFileDescriptor source = new(sourcePath);
OutputDescriptor dest = new(destPath);

using CancellationTokenSource cts = timeoutSeconds is int seconds
    ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
    : new CancellationTokenSource();

Stopwatch sw = Stopwatch.StartNew();
long beforeGen0 = GC.CollectionCount(0);
long beforeGen1 = GC.CollectionCount(1);
long beforeGen2 = GC.CollectionCount(2);
long beforeAllocated = GC.GetTotalAllocatedBytes(precise: false);

IndexResult? result = null;
try
{
    result = await indexer.IndexAsync(source, dest, indexOptions, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine($"Cancelled after {sw.Elapsed.TotalSeconds:F1}s (timeout reached).");
    Console.WriteLine();
}

sw.Stop();
long deltaGen0 = GC.CollectionCount(0) - beforeGen0;
long deltaGen1 = GC.CollectionCount(1) - beforeGen1;
long deltaGen2 = GC.CollectionCount(2) - beforeGen2;
long deltaAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated;

double elapsedSeconds = sw.Elapsed.TotalSeconds;
double mbPerSec = sourceSize / (1024.0 * 1024.0) / elapsedSeconds;

if (result is not null)
{
    Console.WriteLine("Index build:");
    Console.WriteLine($"  Rows:      {result.RowCount:N0}");
    Console.WriteLine($"  Chunks:    {result.ChunkCount:N0}");
    Console.WriteLine($"  Time:      {result.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"  Row rate:  {result.RowCount / elapsedSeconds:N0} rows/s");
    Console.WriteLine($"  Read rate: {mbPerSec:F1} MB/s");
    Console.WriteLine();

    Console.WriteLine($"Output:     {result.OutputPath}");
    Console.WriteLine($"  Size:     {result.BytesWritten:N0} bytes ({result.BytesWritten / (1024.0 * 1024.0):F1} MB)");
    Console.WriteLine();

    Console.WriteLine("Columns indexed:");
    Console.WriteLine($"  Bloom:    {FormatColumns(result.BloomColumns)}");
    Console.WriteLine($"  Sorted:   {FormatColumns(result.SortedColumns)}");
    Console.WriteLine($"  Bitmap:   {FormatColumns(result.BitmapColumns)}");
    if (result.DeferredReindexColumns.Count > 0)
    {
        Console.WriteLine($"  Deferred: {FormatColumns(result.DeferredReindexColumns)}");
    }
    Console.WriteLine();
}
else
{
    Console.WriteLine("Partial run — index build was cancelled before completion.");
    Console.WriteLine($"  Elapsed:  {elapsedSeconds:F1}s");
    if (File.Exists(destPath))
    {
        long partialOutput = new FileInfo(destPath).Length;
        Console.WriteLine($"  Partial output: {partialOutput:N0} bytes ({partialOutput / (1024.0 * 1024.0):F1} MB)");
    }
    Console.WriteLine();
}

Console.WriteLine($"Allocated:   {deltaAllocated / (1024.0 * 1024.0):F1} MB");
if (result?.RowCount > 0)
    Console.WriteLine($"  per row:   {deltaAllocated / (double)result.RowCount:F1} bytes");
Console.WriteLine($"GC gen0:     {deltaGen0}");
Console.WriteLine($"GC gen1:     {deltaGen1}");
Console.WriteLine($"GC gen2:     {deltaGen2}");

return 0;

static string FormatColumns(IReadOnlyList<string> columns) =>
    columns.Count == 0 ? "(none)" : string.Join(", ", columns);
