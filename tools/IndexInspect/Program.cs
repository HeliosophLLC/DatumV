using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.Bloom;
using DatumIngest.Indexing.BTree;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Model;

// index-inspect <index-path>
//   Structural summary of a .datum-index file.
//
// index-inspect <index-path> --distinct <column> [--table <name>] [--out <file>]
//   Emit the distinct values for <column> (one per line), sourced from whichever
//   acceleration structure is available (bitmap > B+Tree > sorted).
//
// index-inspect <index-path> --vocab <column> [--table <name>] [--out <file>]
//   Emit "<value>\t<frequency>" for each distinct value, sorted by frequency
//   descending. Frequencies are exact — bitmap sums PopCount across chunks,
//   B+Tree/Sorted count index entries per key.

Options opts;
try
{
    opts = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  index-inspect <index-path>");
    Console.Error.WriteLine("  index-inspect <index-path> --distinct <column> [--table <name>] [--out <file>]");
    Console.Error.WriteLine("  index-inspect <index-path> --vocab <column> [--table <name>] [--out <file>]");
    return 1;
}

if (!File.Exists(opts.IndexPath))
{
    Console.Error.WriteLine($"Index file not found: {opts.IndexPath}");
    return 1;
}

using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(opts.IndexPath);
SourceIndexSet indexSet = mapped.IndexSet;

if (opts.Mode == Mode.Summary)
{
    return RunSummary(opts.IndexPath, indexSet);
}

return RunExtract(opts, indexSet);

static int RunSummary(string indexPath, SourceIndexSet indexSet)
{
    long indexSize = new FileInfo(indexPath).Length;

    Console.WriteLine($"Index file:    {indexPath}");
    Console.WriteLine($"Size:          {indexSize:N0} bytes ({indexSize / (1024.0 * 1024.0):F1} MB)");
    Console.WriteLine();
    Console.WriteLine($"Tables:        {indexSet.Tables.Count}");
    Console.WriteLine();

    foreach (KeyValuePair<string, SourceIndex> entry in indexSet.Tables)
    {
        PrintTable(entry.Key, entry.Value);
    }

    return 0;
}

static int RunExtract(Options opts, SourceIndexSet indexSet)
{
    if (!TryResolveTable(indexSet, opts.TableName, out string tableName, out SourceIndex? index, out string? error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    if (!HasColumn(index!, opts.Column))
    {
        Console.Error.WriteLine($"Column '{opts.Column}' not found in table '{tableName}'.");
        Console.Error.WriteLine("Available columns:");
        foreach (ColumnInfo col in index!.Schema.Schema.Columns)
        {
            Console.Error.WriteLine($"  {col.Name} ({col.Kind})");
        }
        return 1;
    }

    IEnumerable<(DataValue Value, long Frequency)>? entries = EnumerateFromIndex(index!, opts.Column, opts.Mode);
    if (entries is null)
    {
        Console.Error.WriteLine(
            $"Column '{opts.Column}' has no enumerable index (bitmap, B+Tree, or sorted). " +
            "Full-scan fallback is not implemented.");
        return 1;
    }

    using TextWriter writer = opts.OutputPath is null
        ? Console.Out
        : new StreamWriter(opts.OutputPath);

    if (opts.Mode == Mode.Vocab)
    {
        List<(DataValue Value, long Frequency)> materialized = entries.ToList();
        materialized.Sort((a, b) => b.Frequency.CompareTo(a.Frequency));

        foreach ((DataValue value, long frequency) in materialized)
        {
            writer.Write(FormatSample(value));
            writer.Write('\t');
            writer.WriteLine(frequency);
        }

        if (opts.OutputPath is not null)
        {
            Console.Error.WriteLine($"Wrote {materialized.Count:N0} distinct values to {opts.OutputPath}");
        }
    }
    else // Distinct
    {
        long count = 0;
        foreach ((DataValue value, _) in entries)
        {
            writer.WriteLine(FormatSample(value));
            count++;
        }

        if (opts.OutputPath is not null)
        {
            Console.Error.WriteLine($"Wrote {count:N0} distinct values to {opts.OutputPath}");
        }
    }

    return 0;
}

static bool TryResolveTable(
    SourceIndexSet indexSet,
    string? requestedTable,
    out string tableName,
    out SourceIndex? index,
    out string? error)
{
    if (requestedTable is not null)
    {
        if (indexSet.Tables.TryGetValue(requestedTable, out SourceIndex? found))
        {
            tableName = requestedTable;
            index = found;
            error = null;
            return true;
        }

        tableName = string.Empty;
        index = null;
        error = $"Table '{requestedTable}' not found. Available: {string.Join(", ", indexSet.Tables.Keys)}";
        return false;
    }

    if (indexSet.Tables.Count == 1)
    {
        KeyValuePair<string, SourceIndex> only = indexSet.Tables.First();
        tableName = only.Key;
        index = only.Value;
        error = null;
        return true;
    }

    tableName = string.Empty;
    index = null;
    error = $"Multiple tables in index — specify one with --table <name>. Available: {string.Join(", ", indexSet.Tables.Keys)}";
    return false;
}

static bool HasColumn(SourceIndex index, string columnName)
{
    foreach (ColumnInfo col in index.Schema.Schema.Columns)
    {
        if (string.Equals(col.Name, columnName, StringComparison.Ordinal))
        {
            return true;
        }
    }
    return false;
}

static IEnumerable<(DataValue Value, long Frequency)>? EnumerateFromIndex(
    SourceIndex index, string columnName, Mode mode)
{
    // Bitmap — exact frequencies via PopCount across chunks, cheap.
    if (index.BitmapIndexes is { } bitmapSet
        && bitmapSet.TryGetIndex(columnName, out BitmapColumnIndex? bitmap))
    {
        return EnumerateFromBitmap(bitmap, mode);
    }

    // B+Tree — entries sorted by key; dedupe on key transition, count for freq.
    if (index.BPlusTreeIndexes is { } btreeSet
        && btreeSet.TryGetIndex(columnName, out BPlusTreeColumnIndex? btree))
    {
        return EnumerateSortedEntries(btree.TraverseForward(), mode);
    }

    // Sorted — same shape.
    if (index.MappedSortedIndexes is { } mappedSorted
        && mappedSorted.TryGetValue(columnName, out SortedIndex? sorted))
    {
        return EnumerateSortedEntries(sorted.TraverseForward(), mode);
    }

    return null;
}

static IEnumerable<(DataValue Value, long Frequency)> EnumerateFromBitmap(
    BitmapColumnIndex bitmap, Mode mode)
{
    foreach (DataValue value in bitmap.DistinctValues)
    {
        long frequency = 0;

        if (mode == Mode.Vocab)
        {
            for (int chunk = 0; chunk < bitmap.ChunkCount; chunk++)
            {
                frequency += bitmap.GetChunkBitmap(value, chunk).PopCount();
            }
        }

        yield return (value, frequency);
    }
}

static IEnumerable<(DataValue Value, long Frequency)> EnumerateSortedEntries(
    IEnumerable<ValueIndexEntry> entries, Mode mode)
{
    bool haveCurrent = false;
    DataValue current = default;
    long count = 0;

    foreach (ValueIndexEntry entry in entries)
    {
        if (!haveCurrent)
        {
            current = entry.Key;
            count = 1;
            haveCurrent = true;
            continue;
        }

        if (current.Equals(entry.Key))
        {
            count++;
        }
        else
        {
            yield return (current, mode == Mode.Vocab ? count : 0);
            current = entry.Key;
            count = 1;
        }
    }

    if (haveCurrent)
    {
        yield return (current, mode == Mode.Vocab ? count : 0);
    }
}

static void PrintTable(string tableName, SourceIndex index)
{
    Console.WriteLine($"Table '{tableName}'");
    Console.WriteLine($"  Fingerprint: file size {index.Fingerprint.FileSize:N0}, hash {FormatHash(index.Fingerprint.StripedHash)}");
    Console.WriteLine($"  Schema:      {index.Schema.Schema.Columns.Count} columns, {index.Schema.TotalRowCount:N0} total rows");
    Console.WriteLine($"  Chunks:      {index.Chunks.Count}");

    if (index.Chunks.Count > 0)
    {
        long totalChunkRows = 0;
        foreach (IndexChunk chunk in index.Chunks) totalChunkRows += chunk.RowCount;

        string rowMatch = totalChunkRows == index.Schema.TotalRowCount ? "OK" : $"MISMATCH (sum={totalChunkRows:N0})";
        Console.WriteLine($"  Sum rows:    {totalChunkRows:N0} {rowMatch}");
    }

    Console.WriteLine();
    Console.WriteLine("  Columns:");

    int nameWidth = 0;
    foreach (ColumnInfo col in index.Schema.Schema.Columns)
    {
        int width = col.Name.Length + col.Kind.ToString().Length + 3;  // "Name (Kind)"
        if (width > nameWidth) nameWidth = width;
    }
    if (nameWidth < 24) nameWidth = 24;

    foreach (ColumnInfo col in index.Schema.Schema.Columns)
    {
        string header = $"{col.Name} ({col.Kind})";
        Console.Write($"    {header.PadRight(nameWidth)} ");

        List<string> parts = new();

        // Bloom.
        if (index.BloomFilters is BloomFilterSet bloom && bloom.HasColumn(col.Name))
        {
            BloomFilter[]? filters = bloom.GetColumnFilters(col.Name);
            int populated = 0;
            if (filters is not null)
            {
                foreach (BloomFilter f in filters)
                {
                    if (f is not null && f.BitCount > 0) populated++;
                }
            }
            parts.Add($"bloom: {populated}/{bloom.ChunkCount} chunks");
        }

        // Sorted (mapped).
        if (index.MappedSortedIndexes is { } mappedSorted
            && mappedSorted.TryGetValue(col.Name, out SortedIndex? sorted))
        {
            parts.Add($"sorted: {sorted.EntryCount:N0} entries");
        }

        // B+Tree.
        if (index.BPlusTreeIndexes is { } btreeSet
            && btreeSet.TryGetIndex(col.Name, out BPlusTreeColumnIndex? btree))
        {
            BPlusTreeSectionHeader header2 = btree.Reader.Header;
            parts.Add($"btree: {btree.EntryCount:N0} entries, height={header2.TreeHeight}, pages={header2.PageCount:N0}");
        }

        // Bitmap.
        if (index.BitmapIndexes is { } bitmapSet
            && bitmapSet.TryGetIndex(col.Name, out BitmapColumnIndex? bitmap))
        {
            parts.Add($"bitmap: {bitmap.DistinctValues.Count} distinct");
        }

        // Zone map from chunk 0 (representative).
        string? zoneSample = DescribeFirstChunkZoneMap(col.Name, index);
        if (zoneSample is not null) parts.Add(zoneSample);

        if (parts.Count == 0)
        {
            Console.WriteLine("(no index)");
        }
        else
        {
            Console.WriteLine(string.Join("  ", parts));
        }
    }

    Console.WriteLine();
}

static string? DescribeFirstChunkZoneMap(string columnName, SourceIndex index)
{
    if (index.Chunks.Count == 0) return null;

    IndexChunk firstChunk = index.Chunks[0];
    if (!firstChunk.ColumnStatistics.TryGetValue(columnName, out ChunkColumnStatistics? stats)) return null;

    string? range = null;
    if (stats.Minimum is DataValue minVal && stats.Maximum is DataValue maxVal && !minVal.IsNull && !maxVal.IsNull)
    {
        range = $"[{FormatSample(minVal)}..{FormatSample(maxVal)}]";
    }

    long nullCount = stats.NullCount;
    long cardinality = stats.EstimatedCardinality;

    string core = $"chunk0: nulls={nullCount}, card~{cardinality}";
    return range is null ? core : $"{core} {range}";
}

static string FormatSample(DataValue value)
{
    if (value.IsNull) return "(null)";

    return value.Kind switch
    {
        DataKind.String => FormatString(value),
        DataKind.JsonValue => FormatString(value),
        DataKind.Float32 => value.AsFloat32().ToString("G"),
        DataKind.Float64 => value.AsFloat64().ToString("G"),
        DataKind.Int32 => value.AsInt32().ToString(),
        DataKind.Int64 => value.AsInt64().ToString(),
        DataKind.UInt8 => value.AsUInt8().ToString(),
        DataKind.Int8 => value.AsInt8().ToString(),
        DataKind.Int16 => value.AsInt16().ToString(),
        DataKind.UInt16 => value.AsUInt16().ToString(),
        DataKind.UInt32 => value.AsUInt32().ToString(),
        DataKind.UInt64 => value.AsUInt64().ToString(),
        DataKind.Boolean => value.AsBoolean().ToString().ToLowerInvariant(),
        DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
        DataKind.DateTime => value.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
        DataKind.Uuid => value.AsUuid().ToString(),
        _ => $"<{value.Kind}>",
    };
}

static string FormatString(DataValue value)
{
    if (value.IsInline)
    {
        return value.AsString();
    }
    // Reference-backed strings may require a store to resolve. Indexable strings
    // are always inline by the build-time rule, so this branch shouldn't hit in
    // practice when emitting values pulled from an index.
    return "<non-inline string>";
}

static string FormatHash(byte[] hash)
{
    if (hash is null || hash.Length == 0) return "(empty)";
    int prefix = Math.Min(hash.Length, 4);
    return Convert.ToHexString(hash, 0, prefix) + "...";
}

enum Mode { Summary, Distinct, Vocab }

sealed record Options(
    string IndexPath,
    Mode Mode,
    string? TableName,
    string Column,
    string? OutputPath)
{
    public static Options Parse(string[] args)
    {
        if (args.Length < 1)
        {
            throw new ArgumentException("Missing <index-path>.");
        }

        string indexPath = args[0];
        Mode mode = Mode.Summary;
        string? tableName = null;
        string column = string.Empty;
        string? outputPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--distinct":
                    if (mode != Mode.Summary) throw new ArgumentException("Only one of --distinct or --vocab may be specified.");
                    if (++i >= args.Length) throw new ArgumentException("--distinct requires a column name.");
                    mode = Mode.Distinct;
                    column = args[i];
                    break;

                case "--vocab":
                    if (mode != Mode.Summary) throw new ArgumentException("Only one of --distinct or --vocab may be specified.");
                    if (++i >= args.Length) throw new ArgumentException("--vocab requires a column name.");
                    mode = Mode.Vocab;
                    column = args[i];
                    break;

                case "--table":
                    if (++i >= args.Length) throw new ArgumentException("--table requires a name.");
                    tableName = args[i];
                    break;

                case "--out":
                    if (++i >= args.Length) throw new ArgumentException("--out requires a file path.");
                    outputPath = args[i];
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (mode != Mode.Summary && string.IsNullOrEmpty(column))
        {
            throw new ArgumentException("Column name is required.");
        }

        return new Options(indexPath, mode, tableName, column, outputPath);
    }
}
