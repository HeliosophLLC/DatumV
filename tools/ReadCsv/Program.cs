using System.Diagnostics;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;

string sourcePath = args.Length > 0
    ? args[0]
    : @"E:\Datasets\Open Payments\OP_DTL_GNRL_PGYR2024_P01232026_01102026.csv";

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source file not found: {sourcePath}");
    return 1;
}

long fileSize = new FileInfo(sourcePath).Length;
double fileMB = fileSize / (1024.0 * 1024.0);
Console.WriteLine($"Source:      {sourcePath}");
Console.WriteLine($"Source size: {fileSize:N0} bytes ({fileMB:F1} MB)");
Console.WriteLine();

FormatRegistry registry = new([new CsvFileFormat()]);
PoolBacking backing = new();
Pool pool = new(backing);

FileFormatDescriptor source = new(sourcePath);
SerializationContext context = CreateCatalog(pool);
IFormatDeserializer deserializer = registry.CreateDeserializer(source);

long beforeGen0 = GC.CollectionCount(0);
long beforeGen1 = GC.CollectionCount(1);
long beforeGen2 = GC.CollectionCount(2);
long beforeAllocated = GC.GetTotalAllocatedBytes(precise: false);

Stopwatch sw = Stopwatch.StartNew();
long rowCount = 0;
long batchCount = 0;
long totalArenaBytes = 0;
bool schemaPrinted = false;

await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
{
    if (!schemaPrinted && batch.Count > 0)
    {
        Row firstRow = batch[0];
        Console.WriteLine("Schema:");
        for (int i = 0; i < firstRow.FieldCount; i++)
        {
            Console.WriteLine($"  [{i,2}] {firstRow.ColumnNames[i],-40} {firstRow[i].Kind}");
        }
        Console.WriteLine();
        schemaPrinted = true;
    }

    rowCount += batch.Count;
    batchCount++;
    totalArenaBytes += batch.Arena.BytesWritten;
    pool.ReturnRowBatch(batch);
}

sw.Stop();

long deltaGen0 = GC.CollectionCount(0) - beforeGen0;
long deltaGen1 = GC.CollectionCount(1) - beforeGen1;
long deltaGen2 = GC.CollectionCount(2) - beforeGen2;
long deltaAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated;

double elapsedSeconds = sw.Elapsed.TotalSeconds;
double mbPerSec = fileMB / elapsedSeconds;

Console.WriteLine($"Rows:        {rowCount:N0}");
Console.WriteLine($"Batches:     {batchCount:N0}");
Console.WriteLine($"Arena total: {totalArenaBytes:N0} bytes ({totalArenaBytes / (1024.0 * 1024.0):F1} MB)");
Console.WriteLine($"Time:        {elapsedSeconds:F2}s");
Console.WriteLine($"Row rate:    {rowCount / elapsedSeconds:N0} rows/s");
Console.WriteLine($"Read rate:   {mbPerSec:F1} MB/s");
Console.WriteLine();
Console.WriteLine($"Allocated:   {deltaAllocated / (1024.0 * 1024.0):F1} MB");
Console.WriteLine($"  per row:   {(rowCount > 0 ? deltaAllocated / (double)rowCount : 0):F1} bytes");
Console.WriteLine($"GC gen0:     {deltaGen0}");
Console.WriteLine($"GC gen1:     {deltaGen1}");
Console.WriteLine($"GC gen2:     {deltaGen2}");

return 0;
