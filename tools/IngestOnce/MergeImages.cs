using System.Diagnostics;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Statistics;

namespace Heliosoph.DatumV.Tools.IngestOnce;

/// <summary>
/// Walks a glob of image files and emits a single <c>.datum</c> file with
/// schema <c>(derived_col String, img Image)</c>. The derived column is
/// extracted from each filename via the caller-supplied regex; the image
/// payload is the file's raw bytes (PNG/JPEG/etc. — the manifest builder
/// inspects the magic at stats-collection time).
/// </summary>
internal static class MergeImages
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "Usage: ingest-once merge-images <glob_pattern> <dest.datum> <derived_col_name> <filename_regex>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine(
                "  ingest-once merge-images \"E:/data/train/*.png\" wells_images.datum well_id \"^([0-9a-f]+)\\.png$\"");
            return 1;
        }

        string pattern = args[1];
        string destPath = args[2];
        string derivedColumn = args[3];
        Regex filenameRegex = new(args[4], RegexOptions.Compiled);

        string directory = Path.GetDirectoryName(pattern) ?? ".";
        string leafPattern = Path.GetFileName(pattern);
        string[] sourceFiles = Directory.EnumerateFiles(directory, leafPattern).ToArray();
        Array.Sort(sourceFiles, StringComparer.Ordinal);

        if (sourceFiles.Length == 0)
        {
            Console.Error.WriteLine($"No files matched pattern: {pattern}");
            return 1;
        }

        Console.WriteLine($"Pattern:        {pattern}");
        Console.WriteLine($"Files matched:  {sourceFiles.Length:N0}");
        Console.WriteLine($"Dest:           {destPath}");
        Console.WriteLine($"Derived column: {derivedColumn} (regex: {args[4]})");
        Console.WriteLine();

        PoolBacking backing = new();
        Pool pool = new(backing);

        string sidecarPath = Path.ChangeExtension(destPath, SidecarConstants.FileExtension);
        SidecarWriteStore sidecar = new(sidecarPath);

        await using FileStream outputStream = File.Create(destPath);
        DatumFileWriterV2 writer = new(outputStream, sidecar);

        // Schema is fixed and known up front.
        Schema schema = new(
        [
            new ColumnInfo(derivedColumn, DataKind.String, nullable: false),
            new ColumnInfo("img", DataKind.Image, nullable: false),
        ]);
        ColumnDescriptorV2[] descriptors =
        [
            new ColumnDescriptorV2(derivedColumn, DataKind.String,
                ColumnDescriptorV2.EncoderFor(DataKind.String, isArray: false), IsNullable: false),
            new ColumnDescriptorV2("img", DataKind.Image,
                ColumnDescriptorV2.EncoderFor(DataKind.Image, isArray: false), IsNullable: false),
        ];
        writer.Initialize(descriptors);
        ColumnLookup lookup = new([derivedColumn, "img"]);

        Arena statsArena = new();
        StatisticsCollector statisticsCollector = new();

        Stopwatch sw = Stopwatch.StartNew();
        long totalBytesIn = 0;
        long totalRows = 0;

        try
        {
            for (int fileIndex = 0; fileIndex < sourceFiles.Length; fileIndex++)
            {
                string sourceFile = sourceFiles[fileIndex];
                string fileName = Path.GetFileName(sourceFile);
                Match match = filenameRegex.Match(fileName);
                if (!match.Success || match.Groups.Count < 2)
                {
                    Console.Error.WriteLine(
                        $"WARNING: regex did not match capture group on '{fileName}'; skipping.");
                    continue;
                }
                string derivedValue = match.Groups[1].Value;

                byte[] imageBytes = await File.ReadAllBytesAsync(sourceFile);
                totalBytesIn += imageBytes.Length;

                Arena batchArena = new();
                RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: batchArena);

                DataValue[] row = pool.RentDataValues(2);
                row[0] = DataValue.FromString(derivedValue, batchArena);
                row[1] = DataValue.FromImage(imageBytes, batchArena);
                batch.Add(row);

                writer.WriteRowBatch(batch);

                // Stats: stabilise into the long-lived stats arena so
                // ImageStatsAccumulator's retained values stay readable
                // for the manifest pass.
                DataValue[] statsRow =
                [
                    DataValueRetention.Stabilize(row[0], batchArena, statsArena),
                    DataValueRetention.Stabilize(row[1], batchArena, statsArena),
                ];
                statisticsCollector.AddRow(new Row(lookup, statsRow), statsArena);

                pool.ReturnRowBatch(batch);
                totalRows++;

                if ((fileIndex + 1) % 200 == 0 || fileIndex == sourceFiles.Length - 1)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    Console.WriteLine(
                        $"  [{fileIndex + 1,5:N0}/{sourceFiles.Length:N0}] " +
                        $"{totalBytesIn / (1024.0 * 1024.0):F1} MB read, " +
                        $"{elapsed:F1}s ({totalRows / elapsed:F1} files/s)");
                }
            }

            writer.FinalizeWriter();
        }
        finally
        {
            writer.Dispose();
            sidecar.Dispose();
        }

        sw.Stop();

        long bytesOut = new FileInfo(destPath).Length;
        long sidecarBytes = File.Exists(sidecarPath) ? new FileInfo(sidecarPath).Length : 0;

        Console.WriteLine();
        Console.WriteLine("Totals:");
        Console.WriteLine($"  Rows:       {totalRows:N0}");
        Console.WriteLine($"  Bytes in:   {totalBytesIn:N0} ({totalBytesIn / (1024.0 * 1024.0):F1} MB)");
        Console.WriteLine($"  Bytes out:  {bytesOut:N0} ({bytesOut / (1024.0 * 1024.0):F1} MB)");
        if (sidecarBytes > 0)
        {
            Console.WriteLine($"  Sidecar:    {sidecarBytes:N0} ({sidecarBytes / (1024.0 * 1024.0):F1} MB)");
        }
        Console.WriteLine($"  Time:       {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  File rate:  {totalRows / sw.Elapsed.TotalSeconds:F1} files/s");

        string manifestPath = Path.ChangeExtension(destPath, ".datum-manifest");
        string tableName = Path.GetFileNameWithoutExtension(destPath);
        Dictionary<string, ColumnInfo> columnInfos = new(schema.Columns.Count);
        foreach (ColumnInfo column in schema.Columns)
        {
            columnInfos[column.Name] = column;
        }
        QueryResultsManifest manifest = ManifestBuilder.Build(
            statisticsCollector.GetStatistics(), columnInfos, totalRows);
        await ManifestSerializer.WriteToFileAsync(tableName, manifest, manifestPath);
        Console.WriteLine($"  Manifest:   {manifestPath}");

        statsArena.Dispose();
        return 0;
    }
}
