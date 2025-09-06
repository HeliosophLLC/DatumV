namespace DatumIngest.Tests;

using DatumIngest.Diagnostics;
using DatumIngest.Manifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="DatumIngester"/>, covering both single-table and multi-table sources.
/// </summary>
public sealed class DatumIngesterTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    /// <summary>
    /// Verifies that a single-table source preserves the convenience accessors on
    /// <see cref="DatumIngestionResult"/>.
    /// </summary>
    [Fact]
    public async Task IngestAsync_SingleTableSource_ExposesTableResult()
    {
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        Assert.Single(result.Tables);
        DatumIngestionTableResult table = result.Tables["array_json"];
        Assert.Equal("array_json", table.TableName);
        Assert.Equal(3, result.RowCount);
        Assert.Equal(3, table.Schema.Columns.Count);
        Assert.Single(table.Manifest.Tables);
        Assert.True(table.Manifest.Tables.ContainsKey("array_json"));
        Assert.NotEmpty(table.Manifest.Tables["array_json"].Features);
        Assert.True(table.DatumStream.Length > 0);
        Assert.Single(result.SourceSchema.Tables);
        Assert.Single(result.SourceManifest.Tables);
    }

    /// <summary>
    /// Verifies that a root-object JSON file is ingested as one output per discovered array property,
    /// rather than silently writing only the first table.
    /// </summary>
    [Fact]
    public async Task IngestAsync_MultiTableJson_ReturnsAllDiscoveredTables()
    {
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("root_object.json"), cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Tables.Count);
        Assert.Contains("root_object_json_licenses", result.Tables.Keys);
        Assert.Contains("root_object_json_captions", result.Tables.Keys);
        Assert.Equal(2, result.SourceSchema.Tables.Count);
        Assert.Equal(2, result.SourceManifest.Tables.Count);
        Assert.True(result.Tables["root_object_json_licenses"].DatumStream.Length > 0);
        Assert.True(result.Tables["root_object_json_captions"].DatumStream.Length > 0);
    }

    /// <summary>
    /// Verifies that the per-table <see cref="DatumIngestionTableResult.Manifest"/> is always a
    /// single-entry <see cref="SourceManifest"/> keyed by the table's own
    /// <see cref="DatumIngestionTableResult.TableName"/>.
    /// </summary>
    [Fact]
    public async Task IngestAsync_MultiTableJson_EachTableManifestIsSingleEntryKeyedByTableName()
    {
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("root_object.json"), cancellationToken: CancellationToken.None);

        foreach (DatumIngestionTableResult table in result.Tables.Values)
        {
            Assert.Single(table.Manifest.Tables);
            Assert.True(table.Manifest.Tables.ContainsKey(table.TableName));
        }
    }

    // ──────────────── BuildIndexAsync ────────────────

    /// <summary>
    /// Verifies that <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, CancellationToken)"/>
    /// produces a valid index from an ingested <c>.datum</c> file.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_FromDatumFile_ProducesIndex()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, cancellationToken: CancellationToken.None);

            Assert.Single(indexResult.Tables);
            DatumIndexTableResult indexTable = indexResult.Tables.Values.First();
            Assert.True(indexTable.IndexStream.Length > 0);
            Assert.Single(indexResult.IndexSet.Tables);
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }

    /// <summary>
    /// Round-trip test: ingest a source file, write the <c>.datum</c> to disk,
    /// then build an index from it and verify the index is readable.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_RoundTrip_IngestThenIndex()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_rt_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, cancellationToken: CancellationToken.None);

            DatumIndexTableResult indexTable = indexResult.Tables.Values.First();

            // Write the index stream to a temp file so UnifiedIndexReader can mmap it.
            string tempIndexPath = Path.Combine(Path.GetTempPath(), $"test_ridx_{Guid.NewGuid():N}.datum-index");
            try
            {
                await using (FileStream indexOutput = File.Create(tempIndexPath))
                {
                    await indexTable.IndexStream.CopyToAsync(indexOutput, CancellationToken.None);
                }

                using DatumIngest.Indexing.MappedSourceIndexSet restored =
                    DatumIngest.Indexing.UnifiedIndexReader.Open(tempIndexPath);

                Assert.Single(restored.IndexSet.Tables);
                Assert.Equal(3, restored.IndexSet.Tables.Values.First().Schema.TotalRowCount);
            }
            finally
            {
                if (File.Exists(tempIndexPath))
                {
                    File.Delete(tempIndexPath);
                }
            }
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }

    // ──────────────── Progress reporting ────────────────

    /// <summary>
    /// Verifies that <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, Action{IndexingProgress}?, CancellationToken)"/>
    /// reports progress through the <see cref="Action{IndexingProgress}"/> callback,
    /// ending at 100%.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_WithProgress_ReportsProgressEndingAt100()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_prog_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            List<IndexingProgress> reports = [];

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, progress: reports.Add, cancellationToken: CancellationToken.None);

            Assert.NotEmpty(reports);
            Assert.All(reports, report =>
            {
                Assert.NotEmpty(report.TableName);
                Assert.Equal(3, report.TotalRows);
                Assert.True(report.PercentComplete >= 0 && report.PercentComplete <= 100);
                Assert.True(report.RowsProcessed > 0 && report.RowsProcessed <= 3);
            });
            Assert.Equal(100, reports[^1].PercentComplete);

            // Percentages must be monotonically non-decreasing
            for (int i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i].PercentComplete >= reports[i - 1].PercentComplete);
            }
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }

    /// <summary>
    /// Verifies that the <see cref="Stream"/>-based <c>BuildIndexAsync</c> overload
    /// also reports progress correctly.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_StreamOverload_ReportsProgressEndingAt100()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        List<IndexingProgress> reports = [];


        await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
            "array_json.datum", table.DatumStream, progress: reports.Add, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1].PercentComplete);
        Assert.All(reports, report =>
        {
            Assert.True(report.TotalRows > 0);
            Assert.True(report.PercentComplete >= 0 && report.PercentComplete <= 100);
        });
    }

    /// <summary>
    /// Verifies that passing <c>null</c> for progress does not cause errors
    /// and produces a valid index.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_NullProgress_ProducesValidIndex()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_np_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, progress: null, cancellationToken: CancellationToken.None);

            Assert.Single(indexResult.Tables);
            Assert.True(indexResult.Tables.Values.First().IndexStream.Length > 0);
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }

    // ──────────────────── Ingestion progress ────────────────────

    /// <summary>
    /// Verifies that ingesting a CSV file reports progress ending at 100%.
    /// CSV supports sampling-based row estimation so intermediate reports are expected.
    /// </summary>
    [Fact]
    public async Task IngestAsync_CsvFile_ReportsProgressEndingAt100()
    {
        List<IngestionProgress> reports = [];

        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("simple.csv"), progress: reports.Add, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1].PercentComplete);
        Assert.All(reports, report =>
        {
            Assert.NotEmpty(report.TableName);
            Assert.True(report.PercentComplete >= 0 && report.PercentComplete <= 100);
            Assert.True(report.RowsProcessed >= 0);
        });

        // Percentages must be monotonically non-decreasing.
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].PercentComplete >= reports[i - 1].PercentComplete);
        }
    }

    /// <summary>
    /// Verifies that ingesting a JSON file reports exactly 100% completion.
    /// JSON providers do not estimate row counts, so only the final report is issued.
    /// </summary>
    [Fact]
    public async Task IngestAsync_JsonFile_ReportsProgressAt100()
    {
        List<IngestionProgress> reports = [];


        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("array.json"), progress: reports.Add, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1].PercentComplete);
        Assert.All(reports, report =>
        {
            Assert.NotEmpty(report.TableName);
            Assert.True(report.RowsProcessed > 0);
        });
    }

    /// <summary>
    /// Verifies that passing <c>null</c> for progress does not cause errors
    /// and produces a valid ingestion result.
    /// </summary>
    [Fact]
    public async Task IngestAsync_NullProgress_ProducesValidResult()
    {
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("simple.csv"), progress: null, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(result.Tables);
        Assert.True(result.RowCount > 0);
    }

    /// <summary>
    /// Verifies that the <see cref="Stream"/>-based <c>IngestAsync</c> overload
    /// also reports progress correctly.
    /// </summary>
    [Fact]
    public async Task IngestAsync_StreamOverload_ReportsProgress()
    {
        List<IngestionProgress> reports = [];


        await using FileStream source = File.OpenRead(FixturePath("simple.csv"));
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            "simple.csv", source, progress: reports.Add, cancellationToken: CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(100, reports[^1].PercentComplete);
    }

    // ──────────────────── Diagnostics callback ────────────────────

    /// <summary>
    /// Verifies that the <see cref="DatumIndexerOptions.Diagnostics"/> callback fires
    /// <see cref="IndexingDiagnosticEventKind.ScanningCompleted"/> and
    /// <see cref="IndexingDiagnosticEventKind.IndexWriteCompleted"/> events with correct data.
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_WithDiagnostics_FiresScanAndWriteEvents()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_diag_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            List<IndexingDiagnosticEvent> events = [];

            DatumIndexerOptions options = new() { Diagnostics = events.Add };

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, options, cancellationToken: CancellationToken.None);

            // Must have at least ScanningCompleted and IndexWriteCompleted.
            Assert.Contains(events, e => e.Kind == IndexingDiagnosticEventKind.ScanningCompleted);
            Assert.Contains(events, e => e.Kind == IndexingDiagnosticEventKind.IndexWriteCompleted);

            IndexingDiagnosticEvent scanComplete = events.First(
                e => e.Kind == IndexingDiagnosticEventKind.ScanningCompleted);
            Assert.NotEmpty(scanComplete.TableName);
            Assert.Equal(3, scanComplete.RowsProcessed);
            Assert.True(scanComplete.TotalChunks >= 1);

            IndexingDiagnosticEvent writeComplete = events.First(
                e => e.Kind == IndexingDiagnosticEventKind.IndexWriteCompleted);
            Assert.NotEmpty(writeComplete.TableName);
            Assert.True(writeComplete.BytesWritten > 0);
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }

    /// <summary>
    /// Verifies that <see cref="IndexingDiagnosticEventKind.ChunkFlushed"/> events fire
    /// when multiple chunks are produced (small chunk size forces multiple chunks).
    /// </summary>
    [Fact]
    public async Task BuildIndexAsync_SmallChunkSize_FiresChunkFlushedEvents()
    {
        await using DatumIngestionResult ingestion = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        DatumIngestionTableResult table = ingestion.Tables["array_json"];

        string tempDatumPath = Path.Combine(Path.GetTempPath(), $"test_cf_{Guid.NewGuid():N}.datum");
        try
        {
            await using (FileStream output = File.Create(tempDatumPath))
            {
                await table.DatumStream.CopyToAsync(output, CancellationToken.None);
            }

            List<IndexingDiagnosticEvent> events = [];

            // Chunk size 1 forces a flush per row → 3 rows → 3 ChunkFlushed events.
            DatumIndexerOptions options = new() { ChunkSize = 1, Diagnostics = events.Add };

            await using DatumIndexResult indexResult = await DatumIngester.BuildIndexAsync(
                tempDatumPath, options, cancellationToken: CancellationToken.None);

            List<IndexingDiagnosticEvent> chunkEvents = events
                .Where(e => e.Kind == IndexingDiagnosticEventKind.ChunkFlushed)
                .ToList();

            // With 3 rows and chunk size 1, at least 2 chunks should be flushed mid-scan
            // (the last chunk is finalized in Finalize(), which also fires the callback).
            Assert.True(chunkEvents.Count >= 2, $"Expected ≥2 ChunkFlushed events, got {chunkEvents.Count}.");

            // Chunk indexes should be ascending.
            for (int i = 1; i < chunkEvents.Count; i++)
            {
                Assert.True(chunkEvents[i].ChunkIndex > chunkEvents[i - 1].ChunkIndex);
            }
        }
        finally
        {
            if (File.Exists(tempDatumPath))
            {
                File.Delete(tempDatumPath);
            }
        }
    }
}