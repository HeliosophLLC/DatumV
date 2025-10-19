using System.Diagnostics;
using DatumIngest.Catalog;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Idx;
using DatumIngest.Serialization.Zip;

string sourcePath = args.Length > 0
    ? args[0]
    : @"E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.csv";

string destPath = args.Length > 1
    ? args[1]
    : Path.ChangeExtension(sourcePath, ".datum");

// Optional: third CLI arg = max wall-clock seconds before cancelling.
// Useful for profiling large sources without committing to a full ingest.
int? timeoutSeconds = args.Length > 2 && int.TryParse(args[2], out int t) ? t : null;

// Optional: fourth CLI arg = memory budget preset. "low" selects the
// multi-tenant-server preset (32 MB row groups, serial column encoding,
// 4 MB batch target). Anything else → default (max throughput).
IngestionOptions ingestionOptions = args.Length > 3
    && args[3].Equals("low", StringComparison.OrdinalIgnoreCase)
    ? IngestionOptions.MultiTenantServer
    : IngestionOptions.Default;

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
Console.WriteLine(
    $"Memory:      row-group {ingestionOptions.RowGroupByteThreshold / (1024 * 1024)} MB, " +
    $"{(ingestionOptions.SerialColumnEncoding ? "serial" : "parallel")} encode, " +
    $"batch {ingestionOptions.BatchByteTarget / (1024 * 1024)} MB");
Console.WriteLine();

FormatRegistry registry = new([new CsvFileFormat(), new IdxFileFormat(), new ZipFileFormat()]);
PoolBacking backing = new();
Pool pool = new(backing);
Ingester ingester = new(registry, pool);

using FileFormatDescriptor source = new(sourcePath);
OutputDescriptor dest = new(destPath);

using CancellationTokenSource cts = timeoutSeconds is int seconds
    ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
    : new CancellationTokenSource();

Stopwatch sw = Stopwatch.StartNew();
long beforeGen0 = GC.CollectionCount(0);
long beforeGen1 = GC.CollectionCount(1);
long beforeGen2 = GC.CollectionCount(2);
long beforeAllocated = GC.GetTotalAllocatedBytes(precise: false);

IngestionResult? result = null;
try
{
    result = await ingester.IngestAsync(source, dest, ingestionOptions, cts.Token);
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
    if (result.ScanPass is { } scan)
    {
        Console.WriteLine("Scan pass:");
        Console.WriteLine($"  Rows:      {scan.RowCount:N0}");
        Console.WriteLine($"  Time:      {scan.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Read rate: {scan.BytesRead / (1024.0 * 1024.0) / scan.Elapsed.TotalSeconds:F1} MB/s");
        Console.WriteLine();
    }

    Console.WriteLine("Ingest pass:");
    Console.WriteLine($"  Rows:       {result.IngestPass.RowCount:N0}");
    Console.WriteLine($"  Batches:    {result.IngestPass.BatchCount:N0}");
    Console.WriteLine($"  Arena:      {result.IngestPass.ArenaBytesWritten / (1024.0 * 1024.0):F1} MB");
    Console.WriteLine($"  Time:       {result.IngestPass.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine();

    Console.WriteLine("Totals:");
    Console.WriteLine($"  Rows:       {result.RowCount:N0}");
    Console.WriteLine($"  Bytes out:  {result.BytesWritten:N0} ({result.BytesWritten / (1024.0 * 1024.0):F1} MB)");
    Console.WriteLine($"  Time:       {elapsedSeconds:F1}s");
    Console.WriteLine($"  Row rate:   {result.RowCount / elapsedSeconds:N0} rows/s");
    Console.WriteLine($"  Read rate:  {mbPerSec:F1} MB/s");
    Console.WriteLine();

    // Sidecar manifest: same stem as the .datum output, `.datum-manifest` extension.
    // Matches the convention used by the server's INSERT path and the catalog's
    // sidecar resolver so downstream tools pick it up automatically.
    string manifestPath = Path.ChangeExtension(destPath, ".datum-manifest");
    string tableName = FileFormatDetector.DeriveTableName(sourcePath);
    await ManifestSerializer.WriteToFileAsync(tableName, result.Manifest, manifestPath);
    long manifestBytes = new FileInfo(manifestPath).Length;
    Console.WriteLine($"Manifest:    {manifestPath} ({manifestBytes:N0} bytes)");
    Console.WriteLine();
}
else
{
    Console.WriteLine("Partial run — ingest was cancelled before completion.");
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
