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
    public async Task IngestAsync_SingleTableSource_ExposesConvenienceProperties()
    {
        await using DatumIngestionResult result = await DatumIngester.IngestAsync(
            FixturePath("array.json"), cancellationToken: CancellationToken.None);

        Assert.Single(result.Tables);
        Assert.Equal("array", result.TableName);
        Assert.Equal(3, result.RowCount);
        Assert.Equal(3, result.Schema.Columns.Count);
        Assert.NotEmpty(result.Manifest.Features);
        Assert.True(result.DatumStream.Length > 0);
        Assert.True(result.IndexStream.Length > 0);
        Assert.Single(result.SourceSchema.Tables);
        Assert.Single(result.SourceManifest.Tables);
        Assert.Single(result.IndexSet.Tables);
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
        Assert.Contains("root_object.licenses", result.Tables.Keys);
        Assert.Contains("root_object.captions", result.Tables.Keys);
        Assert.Equal(2, result.SourceSchema.Tables.Count);
        Assert.Equal(2, result.SourceManifest.Tables.Count);
        Assert.Equal(2, result.IndexSet.Tables.Count);
        Assert.True(result.Tables["root_object.licenses"].DatumStream.Length > 0);
        Assert.True(result.Tables["root_object.licenses"].IndexStream.Length > 0);
        Assert.True(result.Tables["root_object.captions"].DatumStream.Length > 0);
        Assert.True(result.Tables["root_object.captions"].IndexStream.Length > 0);
        Assert.Throws<InvalidOperationException>(() => _ = result.TableName);
    }
}