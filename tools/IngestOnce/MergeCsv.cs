using System.Diagnostics;
using System.Text.RegularExpressions;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Statistics;

namespace DatumIngest.Tools.IngestOnce;

/// <summary>
/// Multi-source CSV merge: walks a glob, runs the CSV deserializer per file,
/// prepends a derived String column extracted from each filename via regex,
/// and streams the augmented batches into a single <c>.datum</c> output.
/// One writer, one statistics pass, one manifest — same on-disk shape as a
/// single-file ingest, just with an extra leading column carrying the
/// caller-supplied identifier.
/// </summary>
internal static class MergeCsv
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "Usage: ingest-once merge-csv <glob_pattern> <dest.datum> <derived_col_name> <filename_regex>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine(
                "  ingest-once merge-csv \"E:/data/train/*__typewell.csv\" typewell.datum well_id \"^([0-9a-f]+)__\"");
            return 1;
        }

        string pattern = args[1];
        string destPath = args[2];
        string derivedColumn = args[3];
        Regex filenameRegex = new(args[4], RegexOptions.Compiled);

        // Resolve the glob. Pattern is a directory + leaf wildcard
        // (Directory.EnumerateFiles handles only the leaf component).
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

        FormatRegistry registry = new([new CsvFileFormat()]);
        PoolBacking backing = new();
        Pool pool = new(backing);

        string sidecarPath = Path.ChangeExtension(destPath, SidecarConstants.FileExtension);
        SidecarWriteStore sidecar = new(sidecarPath);
        ulong sidecarFingerprint = sidecar.Fingerprint;

        await using FileStream outputStream = File.Create(destPath);
        DatumFileWriterV2 writer = new(outputStream, sidecar);

        Stopwatch sw = Stopwatch.StartNew();
        long totalRows = 0;
        long totalBytesIn = 0;
        bool writerInitialized = false;
        ColumnLookup? augmentedLookup = null;

        // Long-lived stats arena for cross-file accumulator state (top-K
        // samples etc. retain DataValue offsets — they need an arena that
        // outlives the per-file augmented batches).
        Arena statsArena = new();
        StatisticsCollector statisticsCollector = new();
        Schema? finalSchema = null;

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
                totalBytesIn += new FileInfo(sourceFile).Length;

                using FileFormatDescriptor source = new(sourceFile);
                IFormatDeserializer deserializer = registry.CreateDeserializer(source);
                SerializationContext sourceContext = new(pool, lboStore: sidecar);

                await foreach (RowBatch sourceBatch in deserializer.DeserializeAsync(sourceContext))
                {
                    if (sourceBatch.Count == 0)
                    {
                        pool.ReturnRowBatch(sourceBatch);
                        continue;
                    }

                    if (!writerInitialized)
                    {
                        // First non-empty batch — peek schema, prepend the
                        // derived column, initialise the writer.
                        SchemaDetector detector = new();
                        detector.Detect(sourceBatch);
                        Schema sourceSchema = detector.Schema;

                        List<ColumnInfo> augmentedColumns = new(sourceSchema.Columns.Count + 1)
                        {
                            new ColumnInfo(derivedColumn, DataKind.String, nullable: false),
                        };
                        augmentedColumns.AddRange(sourceSchema.Columns);
                        finalSchema = new Schema(augmentedColumns);

                        ColumnDescriptorV2[] descriptors = ToV2Descriptors(finalSchema);
                        writer.Initialize(descriptors);
                        writerInitialized = true;
                        augmentedLookup = new ColumnLookup(
                            augmentedColumns.Select(c => c.Name).ToArray());
                    }

                    RowBatch augmentedBatch = BuildAugmentedBatch(
                        pool, sourceBatch, augmentedLookup!, derivedColumn, derivedValue,
                        statsArena, statisticsCollector);

                    writer.WriteRowBatch(augmentedBatch);

                    totalRows += augmentedBatch.Count;
                    pool.ReturnRowBatch(sourceBatch);
                    pool.ReturnRowBatch(augmentedBatch);
                }

                if ((fileIndex + 1) % 100 == 0 || fileIndex == sourceFiles.Length - 1)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    Console.WriteLine(
                        $"  [{fileIndex + 1,5:N0}/{sourceFiles.Length:N0}] " +
                        $"{totalRows:N0} rows, {elapsed:F1}s ({totalRows / elapsed:N0} rows/s)");
                }
            }

            if (!writerInitialized)
            {
                Console.Error.WriteLine("No non-empty batches produced from any source file.");
                writer.Initialize(ToV2Descriptors(new Schema([])));
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
        Console.WriteLine();
        Console.WriteLine("Totals:");
        Console.WriteLine($"  Rows:       {totalRows:N0}");
        Console.WriteLine($"  Bytes in:   {totalBytesIn:N0} ({totalBytesIn / (1024.0 * 1024.0):F1} MB)");
        Console.WriteLine($"  Bytes out:  {bytesOut:N0} ({bytesOut / (1024.0 * 1024.0):F1} MB)");
        Console.WriteLine($"  Time:       {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Row rate:   {totalRows / sw.Elapsed.TotalSeconds:N0} rows/s");

        // Manifest. The schema is the augmented one (derived column + source
        // columns); statistics were accumulated against the same shape.
        if (finalSchema is not null)
        {
            string manifestPath = Path.ChangeExtension(destPath, ".datum-manifest");
            string tableName = Path.GetFileNameWithoutExtension(destPath);

            Dictionary<string, ColumnInfo> columnInfos = new(finalSchema.Columns.Count);
            foreach (ColumnInfo column in finalSchema.Columns)
            {
                columnInfos[column.Name] = column;
            }
            QueryResultsManifest manifest = ManifestBuilder.Build(
                statisticsCollector.GetStatistics(), columnInfos, totalRows);
            await ManifestSerializer.WriteToFileAsync(tableName, manifest, manifestPath);
            Console.WriteLine($"  Manifest:   {manifestPath}");
        }

        _ = sidecarFingerprint; // referenced for future sidecar-aware sample preview
        statsArena.Dispose();
        return 0;
    }

    private static RowBatch BuildAugmentedBatch(
        Pool pool,
        RowBatch sourceBatch,
        ColumnLookup augmentedLookup,
        string derivedColumnName,
        string derivedValue,
        Arena statsArena,
        StatisticsCollector statisticsCollector)
    {
        // Each augmented batch owns a fresh arena. Arena-backed values from
        // the source batch (long strings, byte arrays) are stabilised into
        // the new arena via DataValueRetention so the source can be returned
        // independently. The cost is one byte-copy per non-inline value.
        Arena augmentedArena = new();
        DataValue derivedDataValue = DataValue.FromString(derivedValue, augmentedArena);

        RowBatch augmented = pool.RentRowBatch(
            augmentedLookup, capacity: sourceBatch.Count, arena: augmentedArena);

        int sourceColumnCount = sourceBatch.ColumnLookup.Count;
        DataValue[] statsScratch = new DataValue[sourceColumnCount + 1];

        for (int r = 0; r < sourceBatch.Count; r++)
        {
            Row sourceRow = sourceBatch[r];
            DataValue[] augmentedRow = pool.RentDataValues(sourceColumnCount + 1);
            augmentedRow[0] = derivedDataValue;
            for (int c = 0; c < sourceColumnCount; c++)
            {
                augmentedRow[c + 1] = DataValueRetention.Stabilize(
                    sourceRow[c], sourceBatch.Arena, augmentedArena);
            }
            augmented.Add(augmentedRow);

            // Accumulate statistics against the long-lived stats arena.
            statsScratch[0] = DataValueRetention.Stabilize(derivedDataValue, augmentedArena, statsArena);
            for (int c = 0; c < sourceColumnCount; c++)
            {
                statsScratch[c + 1] = DataValueRetention.Stabilize(
                    sourceRow[c], sourceBatch.Arena, statsArena);
            }
            statisticsCollector.AddRow(new Row(augmentedLookup, statsScratch), statsArena);
        }

        return augmented;
    }

    private static ColumnDescriptorV2[] ToV2Descriptors(Schema schema)
    {
        ColumnDescriptorV2[] descriptors = new ColumnDescriptorV2[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo col = schema.Columns[i];
            descriptors[i] = new ColumnDescriptorV2(
                Name: col.Name,
                Kind: col.Kind,
                Encoder: ColumnDescriptorV2.EncoderFor(col.Kind, col.IsArray),
                IsNullable: col.Nullable,
                IsArray: col.IsArray);
        }
        return descriptors;
    }
}
