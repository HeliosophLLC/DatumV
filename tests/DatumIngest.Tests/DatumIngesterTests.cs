namespace DatumIngest.Tests;

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
        DatumIngestionTableResult table = result.Tables["array.json"];
        Assert.Equal("array.json", table.TableName);
        Assert.Equal(3, result.RowCount);
        Assert.Equal(3, table.Schema.Columns.Count);
        Assert.Single(table.Manifest.Tables);
        Assert.True(table.Manifest.Tables.ContainsKey("array.json"));
        Assert.NotEmpty(table.Manifest.Tables["array.json"].Features);
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
        Assert.Contains("root_object.json.licenses", result.Tables.Keys);
        Assert.Contains("root_object.json.captions", result.Tables.Keys);
        Assert.Equal(2, result.SourceSchema.Tables.Count);
        Assert.Equal(2, result.SourceManifest.Tables.Count);
        Assert.True(result.Tables["root_object.json.licenses"].DatumStream.Length > 0);
        Assert.True(result.Tables["root_object.json.captions"].DatumStream.Length > 0);
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

        DatumIngestionTableResult table = ingestion.Tables["array.json"];

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

        DatumIngestionTableResult table = ingestion.Tables["array.json"];

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

            // Verify the index stream is deserializable
            DatumIngest.Indexing.IndexReader reader = new();
            DatumIngest.Indexing.SourceIndexSet restored = reader.Read(indexTable.IndexStream);

            Assert.Single(restored.Tables);
            Assert.Equal(3, restored.Tables.Values.First().Schema.TotalRowCount);
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