using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;
using Heliosoph.DatumV.Serialization.Idx;
using ICSharpCode.SharpZipLib.BZip2;

namespace Heliosoph.DatumV.Tests.Serialization;

/// <summary>
/// Mirrors <see cref="GzipSupportTests"/> for bzip2. Covers compression detection,
/// logical-path derivation, temp-file materialization, size-cap enforcement, and
/// end-to-end <c>.csv.bz2</c> ingestion. The motivating real-world case is
/// LJSpeech, which only ships as <c>.tar.bz2</c>.
/// </summary>
public sealed class Bzip2SupportTests : ServiceTestBase
{
    [Fact]
    public void Bz2Csv_DetectsBzip2_AndStripsWrapperForLogicalPaths()
    {
        using FileFormatDescriptor descriptor = new("foo.csv.bz2");

        Assert.Equal(CompressionKind.Bzip2, descriptor.Compression);
        Assert.Equal(".csv", descriptor.LogicalExtension);
        Assert.Equal("foo.csv", descriptor.LogicalFileName);
    }

    [Fact]
    public void Bz2TarFilename_StripsBz2ForLogicalExtension()
    {
        using FileFormatDescriptor descriptor = new("LJSpeech-1.1.tar.bz2");

        Assert.Equal(CompressionKind.Bzip2, descriptor.Compression);
        Assert.Equal(".tar", descriptor.LogicalExtension);
        Assert.Equal("LJSpeech-1.1.tar", descriptor.LogicalFileName);
    }

    [Fact]
    public void CsvFileFormat_AcceptsCsvBz2()
    {
        CsvFileFormat format = new();
        using FileFormatDescriptor descriptor = new("foo.csv.bz2");

        Assert.True(format.CanHandle(descriptor, out _));
    }

    [Fact]
    public void IdxFileFormat_AcceptsBz2Filename()
    {
        IdxFileFormat format = new();
        using FileFormatDescriptor descriptor = new("train-images-idx3-ubyte.bz2");

        Assert.True(format.CanHandle(descriptor, out _));
    }

    [Fact]
    public async Task OpenAsync_Bz2Csv_MaterializesAndReadsPlaintext()
    {
        const string csvContent = "id,name\n1,alice\n2,bob\n";
        string bz2Path = Path.Combine(
            Path.GetTempPath(), $"datumv-test-{Guid.NewGuid():N}.csv.bz2");

        WriteBz2File(bz2Path, csvContent);

        try
        {
            using FileFormatDescriptor descriptor = new(bz2Path);

            await using Stream stream = await descriptor.OpenAsync();
            using StreamReader reader = new(stream);
            string actual = await reader.ReadToEndAsync();

            Assert.Equal(csvContent, actual);
            Assert.NotEqual(bz2Path, descriptor.EffectivePath);
            Assert.True(File.Exists(descriptor.EffectivePath));
        }
        finally
        {
            File.Delete(bz2Path);
        }
    }

    [Fact]
    public async Task Dispose_DeletesMaterializedTempFile()
    {
        const string csvContent = "a\n1\n";
        string bz2Path = Path.Combine(
            Path.GetTempPath(), $"datumv-test-{Guid.NewGuid():N}.csv.bz2");

        WriteBz2File(bz2Path, csvContent);

        string materializedPath;
        try
        {
            FileFormatDescriptor descriptor = new(bz2Path);
            await using (Stream _ = await descriptor.OpenAsync())
            {
            }
            materializedPath = descriptor.EffectivePath;
            Assert.True(File.Exists(materializedPath));

            descriptor.Dispose();
            Assert.False(File.Exists(materializedPath));
        }
        finally
        {
            File.Delete(bz2Path);
        }
    }

    [Fact]
    public async Task OpenAsync_ExceedsMaxDecompressedBytes_ThrowsAndCleansUp()
    {
        // 4 KiB of zeros compresses to a tiny bz2 payload; we cap the descriptor at
        // 1 KiB to force the decompressed-size guard to trip.
        byte[] payload = new byte[4096];
        string bz2Path = Path.Combine(
            Path.GetTempPath(), $"datumv-test-{Guid.NewGuid():N}.bin.bz2");

        WriteBz2File(bz2Path, payload);

        try
        {
            using FileFormatDescriptor descriptor = new(
                bz2Path, options: null, maxDecompressedBytes: 1024);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await using Stream _ = await descriptor.OpenAsync();
            });

            Assert.Equal(bz2Path, descriptor.EffectivePath);
        }
        finally
        {
            File.Delete(bz2Path);
        }
    }

    [Fact]
    public async Task CsvBz2_IngestsEndToEnd_ViaCompressedDescriptor()
    {
        const string csv =
            "id,score,name\n" +
            "1,1.5,alice\n" +
            "2,2.7,bob\n";

        string bz2Path = Path.Combine(
            Path.GetTempPath(), $"datumv-test-{Guid.NewGuid():N}.csv.bz2");
        string datumPath = Path.Combine(
            Path.GetTempPath(), $"datumv-test-{Guid.NewGuid():N}.datum");

        WriteBz2File(bz2Path, csv);

        try
        {
            using FileFormatDescriptor source = new(bz2Path);
            OutputDescriptor destination = new(datumPath);

            FormatRegistry registry = new([new CsvFileFormat()]);
            Pooling.Pool pool = CreatePool();
            Ingester ingester = new(registry, pool);

            IngestionResult result = await ingester.IngestAsync(source, destination);

            Assert.Equal(2, result.RowCount);
            Assert.Equal(3, result.Schema.Columns.Count);
            Assert.True(result.BytesWritten > 0);
            Assert.True(File.Exists(datumPath));
        }
        finally
        {
            File.Delete(bz2Path);
            if (File.Exists(datumPath)) File.Delete(datumPath);
        }
    }

    private static void WriteBz2File(string path, string content)
    {
        WriteBz2File(path, System.Text.Encoding.UTF8.GetBytes(content));
    }

    private static void WriteBz2File(string path, byte[] content)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using BZip2OutputStream bz = new(fs);
        bz.Write(content, 0, content.Length);
    }
}
