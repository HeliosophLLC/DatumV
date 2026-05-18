using DatumIngest.Serialization;
using DatumIngest.Serialization.Fits;

namespace DatumIngest.Tests.Serialization.Fits;

/// <summary>
/// Unit tests for <see cref="FitsFileFormat.CanHandle"/> covering the
/// extension-based and magic-byte-based detection rules. The magic-byte
/// path is the load-bearing one for FITS files with non-standard
/// extensions or no extension at all (common with HEASARC mission
/// archives).
/// </summary>
public sealed class FitsFileFormatTests
{
    [Theory]
    [InlineData("data.fits")]
    [InlineData("DATA.FITS")]
    [InlineData("data.fit")]
    [InlineData("data.fts")]
    public void CanHandle_MatchesFitsExtensions(string fileName)
    {
        FitsFileFormat format = new();
        byte[] file = BuildMinimalFitsFile();
        using MemoryFileDescriptor descriptor = new(file, fileName);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<FitsDeserializer>(deserializer);
    }

    [Fact]
    public void CanHandle_RejectsNonFitsExtensions()
    {
        FitsFileFormat format = new();
        using MemoryFileDescriptor descriptor = new("not fits content", "data.csv");

        Assert.False(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.Null(deserializer);
    }

    [Fact]
    public void CanHandle_MatchesFitsMagicAtOffsetZero_WithUnknownExtension()
    {
        // A real FITS file mislabelled as .dat — magic bytes still
        // unambiguously identify it.
        FitsFileFormat format = new();
        byte[] file = BuildMinimalFitsFile();
        string path = Path.Combine(Path.GetTempPath(), $"fits-magic-{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, file);
        try
        {
            using FileFormatDescriptor descriptor = new(path);
            Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
            Assert.IsType<FitsDeserializer>(deserializer);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CanHandle_RejectsNonFitsBytes_WithUnknownExtension()
    {
        FitsFileFormat format = new();
        byte[] notFits = "this is not a fits file at all"u8.ToArray();
        string path = Path.Combine(Path.GetTempPath(), $"not-fits-{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, notFits);
        try
        {
            using FileFormatDescriptor descriptor = new(path);
            Assert.False(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
            Assert.Null(deserializer);
        }
        finally { File.Delete(path); }
    }

    private static byte[] BuildMinimalFitsFile() =>
        new FitsTestBuilder()
            .BeginPrimary()
                .Int("BITPIX", 8)
                .Int("NAXIS", 0)
            .EndHdu()
            .Build();
}
