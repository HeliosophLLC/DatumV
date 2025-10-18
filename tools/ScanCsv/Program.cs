using DatumIngest.Manifest;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

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

long beforeAllocated = GC.GetTotalAllocatedBytes(precise: false);
long beforeGen0 = GC.CollectionCount(0);
long beforeGen1 = GC.CollectionCount(1);
long beforeGen2 = GC.CollectionCount(2);

FileFormatDescriptor source = new(sourcePath);
CsvScanResult scan = await CsvTypeScanner.ScanAsync(source);

long deltaAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated;
long deltaGen0 = GC.CollectionCount(0) - beforeGen0;
long deltaGen1 = GC.CollectionCount(1) - beforeGen1;
long deltaGen2 = GC.CollectionCount(2) - beforeGen2;

double elapsedSeconds = scan.Elapsed.TotalSeconds;
double mbPerSec = fileMB / elapsedSeconds;

Console.WriteLine("Scanned schema:");
Console.WriteLine($"  {"#",-4} {"Column",-40} {"Kind",-10} {"NullCount",-12} Decision");
Console.WriteLine($"  {new string('-', 80)}");
for (int i = 0; i < scan.ColumnNames.Length; i++)
{
    SchemaInferenceDecision? d = scan.Decisions[i];
    string reason = d?.Reason.ToString() ?? "(none)";
    string severity = d?.Severity.ToString() ?? "";
    Console.WriteLine(
        $"  [{i,2}] {scan.ColumnNames[i],-40} {scan.Kinds[i],-10} {scan.NullCountsPerColumn[i],-12:N0} {reason} ({severity})");
}

Console.WriteLine();
Console.WriteLine("Noteworthy decisions (Notable + Warning):");
bool anyNoteworthy = false;
for (int i = 0; i < scan.ColumnNames.Length; i++)
{
    SchemaInferenceDecision? d = scan.Decisions[i];
    if (d is null || d.Severity == SchemaInferenceSeverity.Routine) continue;
    anyNoteworthy = true;
    Console.WriteLine($"  [{scan.Kinds[i]}] {scan.ColumnNames[i]}:");
    Console.WriteLine($"      {d.Explanation}");
    if (d.Evidence is not null)
    {
        foreach (var kvp in d.Evidence)
        {
            Console.WriteLine($"      • {kvp.Key} = {kvp.Value}");
        }
    }
}
if (!anyNoteworthy) Console.WriteLine("  (none)");

Console.WriteLine();
Console.WriteLine($"Rows:        {scan.RowCount:N0}");
Console.WriteLine($"Bytes read:  {scan.BytesRead:N0} bytes ({scan.BytesRead / (1024.0 * 1024.0):F1} MB)");
Console.WriteLine($"Time:        {elapsedSeconds:F2}s");
Console.WriteLine($"Row rate:    {scan.RowCount / elapsedSeconds:N0} rows/s");
Console.WriteLine($"Read rate:   {mbPerSec:F1} MB/s");
Console.WriteLine();
Console.WriteLine($"Allocated:   {deltaAllocated / (1024.0 * 1024.0):F1} MB");
Console.WriteLine($"  per row:   {(scan.RowCount > 0 ? deltaAllocated / (double)scan.RowCount : 0):F1} bytes");
Console.WriteLine($"GC gen0:     {deltaGen0}");
Console.WriteLine($"GC gen1:     {deltaGen1}");
Console.WriteLine($"GC gen2:     {deltaGen2}");

return 0;
