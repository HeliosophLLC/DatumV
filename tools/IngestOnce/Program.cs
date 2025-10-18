using System.Diagnostics;
using DatumIngest.Ingestion;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

string sourcePath = args.Length > 0
    ? args[0]
    : @"E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.csv";

string destPath = args.Length > 1
    ? args[1]
    : Path.ChangeExtension(sourcePath, ".datum");

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source file not found: {sourcePath}");
    return 1;
}

long sourceSize = new FileInfo(sourcePath).Length;
Console.WriteLine($"Source:      {sourcePath}");
Console.WriteLine($"Dest:        {destPath}");
Console.WriteLine($"Source size: {sourceSize:N0} bytes ({sourceSize / (1024.0 * 1024.0):F1} MB)");
Console.WriteLine();

FormatRegistry registry = new([new CsvFileFormat()]);
PoolBacking backing = new();
Pool pool = new(backing);
Ingester ingester = new(registry, pool);

FileFormatDescriptor source = new(sourcePath);
OutputDescriptor dest = new(destPath);

Stopwatch sw = Stopwatch.StartNew();
long beforeGen0 = GC.CollectionCount(0);
long beforeGen1 = GC.CollectionCount(1);
long beforeGen2 = GC.CollectionCount(2);
long beforeAllocated = GC.GetTotalAllocatedBytes(precise: false);

IngestionResult result = await ingester.IngestAsync(source, dest);

sw.Stop();
long deltaGen0 = GC.CollectionCount(0) - beforeGen0;
long deltaGen1 = GC.CollectionCount(1) - beforeGen1;
long deltaGen2 = GC.CollectionCount(2) - beforeGen2;
long deltaAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated;

double elapsedSeconds = sw.Elapsed.TotalSeconds;
double mbPerSec = sourceSize / (1024.0 * 1024.0) / elapsedSeconds;

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
Console.WriteLine($"Allocated:   {deltaAllocated / (1024.0 * 1024.0):F1} MB");
Console.WriteLine($"  per row:   {deltaAllocated / (double)result.RowCount:F1} bytes");
Console.WriteLine($"GC gen0:     {deltaGen0}");
Console.WriteLine($"GC gen1:     {deltaGen1}");
Console.WriteLine($"GC gen2:     {deltaGen2}");

return 0;
