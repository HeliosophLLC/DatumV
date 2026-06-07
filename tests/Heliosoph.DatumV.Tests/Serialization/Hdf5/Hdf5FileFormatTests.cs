using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;

namespace Heliosoph.DatumV.Tests.Serialization.Hdf5;

/// <summary>
/// Unit tests for <see cref="Hdf5FileFormat.CanHandle"/> covering the
/// extension-based and magic-byte-based detection rules. The magic-byte
/// path is critical for HDF5 files with non-standard or missing
/// extensions (common with HuggingFace dataset shards).
/// </summary>
public sealed class Hdf5FileFormatTests : IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"h5-format-{Guid.NewGuid():N}");

    public Hdf5FileFormatTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string TempH5(string name) => Path.Combine(_scratchDir, name);

    [Theory]
    [InlineData("data.h5")]
    [InlineData("DATA.HDF5")]
    [InlineData("data.hdf")]
    public void CanHandle_MatchesHdf5Extensions(string fileName)
    {
        Hdf5FileFormat format = new();
        string path = TempH5(fileName);
        new H5File { ["x"] = new int[] { 1 } }.Write(path);
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<Hdf5Deserializer>(deserializer);
    }

    [Fact]
    public void CanHandle_MatchesMagicBytes_OnUnknownExtension()
    {
        Hdf5FileFormat format = new();
        // Real HDF5 file mislabelled as .dat — magic signature still detects.
        string path = TempH5("real.dat");
        new H5File { ["x"] = new int[] { 1 } }.Write(path);
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<Hdf5Deserializer>(deserializer);
    }

    [Fact]
    public void CanHandle_RejectsNonHdf5Bytes()
    {
        Hdf5FileFormat format = new();
        string path = TempH5("not.dat");
        File.WriteAllBytes(path, "this is plain text not hdf5"u8.ToArray());
        using FileFormatDescriptor descriptor = new(path);

        Assert.False(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.Null(deserializer);
    }
}
