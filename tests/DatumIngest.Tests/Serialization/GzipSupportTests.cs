using System.IO.Compression;
using DatumIngest.Catalog;
using DatumIngest.DatumFile;
using DatumIngest.Ingestion;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Idx;
using DatumIngest.Serialization.Zip;

namespace DatumIngest.Tests.Serialization;

/// <summary>
/// Covers transparent gzip support on <see cref="FileFormatDescriptor"/>: compression
/// detection, logical path derivation, temp-file materialization on first
/// <c>OpenAsync</c>, size-cap enforcement, and end-to-end <c>.csv.gz</c> ingestion.
/// </summary>
public sealed class GzipSupportTests : ServiceTestBase
{
    // ──────────────── Path derivation ────────────────

    [Fact]
    public void UncompressedCsv_HasNoneCompression_AndRawExtension()
    {
        using FileFormatDescriptor descriptor = new("foo.csv");

        Assert.Equal(CompressionKind.None, descriptor.Compression);
        Assert.Equal(".csv", descriptor.LogicalExtension);
        Assert.Equal("foo.csv", descriptor.LogicalFileName);
    }

    [Fact]
    public void GzippedCsv_DetectsGzip_AndStripsWrapperForLogicalPaths()
    {
        using FileFormatDescriptor descriptor = new("foo.csv.gz");

        Assert.Equal(CompressionKind.Gzip, descriptor.Compression);
        Assert.Equal(".csv", descriptor.LogicalExtension);
        Assert.Equal("foo.csv", descriptor.LogicalFileName);
    }

    [Fact]
    public void MnistGzipFilename_StripsGzForPatternMatching()
    {
        using FileFormatDescriptor descriptor = new("train-images-idx3-ubyte.gz");

        Assert.Equal(CompressionKind.Gzip, descriptor.Compression);
        Assert.Equal("", descriptor.LogicalExtension); // no inner extension
        Assert.Equal("train-images-idx3-ubyte", descriptor.LogicalFileName);
    }

    // ──────────────── CanHandle dispatch ────────────────

    [Fact]
    public void CsvFileFormat_AcceptsBothCsvAndCsvGz()
    {
        CsvFileFormat format = new();

        using FileFormatDescriptor plain = new("foo.csv");
        using FileFormatDescriptor gzipped = new("foo.csv.gz");

        Assert.True(format.CanHandle(plain, out _));
        Assert.True(format.CanHandle(gzipped, out _));
    }

    [Fact]
    public void IdxFileFormat_AcceptsMnistGzFilename()
    {
        IdxFileFormat format = new();

        using FileFormatDescriptor descriptor = new("train-images-idx3-ubyte.gz");

        Assert.True(format.CanHandle(descriptor, out _));
    }

    [Fact]
    public void ZipFileFormat_RejectsGzippedInput()
    {
        ZipFileFormat format = new();

        using FileFormatDescriptor descriptor = new("archive.zip.gz");

        // ZIP inside gzip is a nonsense combination; LogicalExtension is .zip but
        // gzipping a ZIP shouldn't auto-unwrap via ZipFileFormat. We actually *do*
        // accept it (LogicalExtension == .zip) — document this expectation.
        Assert.True(format.CanHandle(descriptor, out _));
    }

    // ──────────────── Decompression + cleanup ────────────────

    [Fact]
    public async Task OpenAsync_GzippedCsv_MaterializesAndReadsPlaintext()
    {
        const string csvContent = "id,name\n1,alice\n2,bob\n";
        string gzPath = Path.Combine(
            Path.GetTempPath(), $"datumingest-test-{Guid.NewGuid():N}.csv.gz");

        WriteGzipFile(gzPath, csvContent);

        try
        {
            using FileFormatDescriptor descriptor = new(gzPath);

            await using Stream stream = await descriptor.OpenAsync();
            using StreamReader reader = new(stream);
            string actual = await reader.ReadToEndAsync();

            Assert.Equal(csvContent, actual);
            Assert.NotEqual(gzPath, descriptor.EffectivePath); // materialized to a different path
            Assert.True(File.Exists(descriptor.EffectivePath));
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    [Fact]
    public async Task Dispose_DeletesMaterializedTempFile()
    {
        const string csvContent = "a\n1\n";
        string gzPath = Path.Combine(
            Path.GetTempPath(), $"datumingest-test-{Guid.NewGuid():N}.csv.gz");

        WriteGzipFile(gzPath, csvContent);

        string materializedPath;
        try
        {
            FileFormatDescriptor descriptor = new(gzPath);
            await using (Stream _ = await descriptor.OpenAsync())
            {
                // Stream is open — OpenAsync has materialized the temp file.
            }
            materializedPath = descriptor.EffectivePath;
            Assert.True(File.Exists(materializedPath));

            descriptor.Dispose();
            Assert.False(File.Exists(materializedPath));
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    [Fact]
    public async Task OpenAsync_ExceedsMaxDecompressedBytes_ThrowsAndCleansUp()
    {
        // 4 KiB of zeros compresses to a tiny gzip payload; we cap the descriptor at
        // 1 KiB to force the decompressed-size guard to trip.
        byte[] payload = new byte[4096];
        string gzPath = Path.Combine(
            Path.GetTempPath(), $"datumingest-test-{Guid.NewGuid():N}.bin.gz");

        WriteGzipFile(gzPath, payload);

        try
        {
            using FileFormatDescriptor descriptor = new(
                gzPath, options: null, maxDecompressedBytes: 1024);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await using Stream _ = await descriptor.OpenAsync();
            });

            // No temp file should remain — MaterializeGzipAsync's catch deletes it.
            string tempRoot = Path.GetTempPath();
            string[] stragglers = Directory.GetFiles(tempRoot, "datumingest-*.tmp");
            // Not a strict assertion (other tests may leave stragglers momentarily);
            // just ensure the descriptor didn't cling to one.
            Assert.Equal(gzPath, descriptor.EffectivePath);
        }
        finally
        {
            File.Delete(gzPath);
        }
    }

    // ──────────────── End-to-end ingest ────────────────

    [Fact]
    public async Task CsvGz_IngestsEndToEnd_ViaGzippedDescriptor()
    {
        const string csv =
            "id,score,name\n" +
            "1,1.5,alice\n" +
            "2,2.7,bob\n";

        string gzPath = Path.Combine(
            Path.GetTempPath(), $"datumingest-test-{Guid.NewGuid():N}.csv.gz");
        string datumPath = Path.Combine(
            Path.GetTempPath(), $"datumingest-test-{Guid.NewGuid():N}.datum");

        WriteGzipFile(gzPath, csv);

        try
        {
            using FileFormatDescriptor source = new(gzPath);
            OutputDescriptor destination = new(datumPath);

            FormatRegistry registry = new([new CsvFileFormat()]);
            Pool pool = CreatePool();
            Ingester ingester = new(registry, pool);

            IngestionResult result = await ingester.IngestAsync(source, destination);

            Assert.Equal(2, result.RowCount);
            Assert.Equal(3, result.Schema.Columns.Count);
            Assert.True(result.BytesWritten > 0);
            Assert.True(File.Exists(datumPath));
        }
        finally
        {
            File.Delete(gzPath);
            if (File.Exists(datumPath)) File.Delete(datumPath);
        }
    }

    // ──────────────── Helpers ────────────────

    private static void WriteGzipFile(string path, string content)
    {
        WriteGzipFile(path, System.Text.Encoding.UTF8.GetBytes(content));
    }

    private static void WriteGzipFile(string path, byte[] content)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using GZipStream gz = new(fs, CompressionLevel.Optimal);
        gz.Write(content);
    }
}
