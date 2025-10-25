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
