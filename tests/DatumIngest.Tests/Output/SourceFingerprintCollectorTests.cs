using DatumIngest.Catalog;
using DatumIngest.Output.Checkpoint;

namespace DatumIngest.Tests.Output;

/// <summary>
/// Tests for <see cref="SourceFingerprintCollector"/>.
/// </summary>
public sealed class SourceFingerprintCollectorTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fingerprint_{Guid.NewGuid():N}");

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void Collect_ReturnsFingerprints_WithCorrectSizeAndProvider()
    {
        string filePath = Path.Combine(_tempDir, "data.csv");
        File.WriteAllText(filePath, "id,name\n1,Alice\n2,Bob\n");

        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", filePath, new Dictionary<string, string>()));

        IReadOnlyList<SourceFingerprint> fingerprints = SourceFingerprintCollector.Collect(catalog);

        Assert.Single(fingerprints);
        Assert.Equal("data", fingerprints[0].Name);
        Assert.Equal("csv", fingerprints[0].Provider);
        Assert.Equal(filePath, fingerprints[0].Path);
        Assert.True(fingerprints[0].SizeBytes > 0);
    }

    [Fact]
    public void Collect_NonexistentFile_ReturnsZeroSizeFingerprint()
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "missing", "/no/such/file.csv", new Dictionary<string, string>()));

        IReadOnlyList<SourceFingerprint> fingerprints = SourceFingerprintCollector.Collect(catalog);

        Assert.Single(fingerprints);
        Assert.Equal(0, fingerprints[0].SizeBytes);
        Assert.Equal(DateTime.MinValue, fingerprints[0].LastModifiedUtc);
    }

    [Fact]
    public void Validate_MatchingFingerprints_ReturnsNull()
    {
        DateTime timestamp = new(2026, 3, 17, 12, 0, 0, DateTimeKind.Utc);
        List<SourceFingerprint> expected = [new("data", "csv", "data.csv", 12345, timestamp)];
        List<SourceFingerprint> current = [new("data", "csv", "data.csv", 12345, timestamp)];

        string? result = SourceFingerprintCollector.Validate(expected, current);

        Assert.Null(result);
    }

    [Fact]
    public void Validate_SizeMismatch_ReturnsErrorDetail()
    {
        DateTime timestamp = new(2026, 3, 17, 12, 0, 0, DateTimeKind.Utc);
        List<SourceFingerprint> expected = [new("data", "csv", "data.csv", 12345, timestamp)];
        List<SourceFingerprint> current = [new("data", "csv", "data.csv", 99999, timestamp)];

        string? result = SourceFingerprintCollector.Validate(expected, current);

        Assert.NotNull(result);
        Assert.Contains("data", result);
        Assert.Contains("size changed", result);
    }

    [Fact]
    public void Validate_TimestampMismatch_ReturnsErrorDetail()
    {
        List<SourceFingerprint> expected =
            [new("data", "csv", "data.csv", 12345, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))];
        List<SourceFingerprint> current =
            [new("data", "csv", "data.csv", 12345, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))];

        string? result = SourceFingerprintCollector.Validate(expected, current);

        Assert.NotNull(result);
        Assert.Contains("modification time changed", result);
    }

    [Fact]
    public void Validate_MissingSource_ReturnsErrorDetail()
    {
        DateTime timestamp = new(2026, 3, 17, 12, 0, 0, DateTimeKind.Utc);
        List<SourceFingerprint> expected = [new("data", "csv", "data.csv", 12345, timestamp)];
        List<SourceFingerprint> current = [];

        string? result = SourceFingerprintCollector.Validate(expected, current);

        Assert.NotNull(result);
        Assert.Contains("missing", result);
    }
}
