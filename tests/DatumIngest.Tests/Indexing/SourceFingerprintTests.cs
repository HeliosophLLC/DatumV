using DatumIngest.Indexing;

namespace DatumIngest.Tests.Indexing;

public sealed class SourceFingerprintTests
{
    [Fact]
    public async Task ComputeAsync_ProducesConsistentHash()
    {
        byte[] data = new byte[1024];
        Random.Shared.NextBytes(data);

        using MemoryStream stream1 = new(data);
        using MemoryStream stream2 = new(data);

        SourceFingerprint first = await SourceFingerprint.ComputeAsync(stream1, CancellationToken.None);
        SourceFingerprint second = await SourceFingerprint.ComputeAsync(stream2, CancellationToken.None);

        Assert.Equal(first.FileSize, second.FileSize);
        Assert.Equal(first.StripedHash, second.StripedHash);
    }

    [Fact]
    public async Task ComputeAsync_CapturesFileSize()
    {
        byte[] data = new byte[2048];
        using MemoryStream stream = new(data);

        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream, CancellationToken.None);

        Assert.Equal(2048, fingerprint.FileSize);
    }

    [Fact]
    public async Task ComputeAsync_ProducesSha256Hash()
    {
        byte[] data = new byte[100];
        using MemoryStream stream = new(data);

        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream, CancellationToken.None);

        Assert.Equal(32, fingerprint.StripedHash.Length);
    }

    [Fact]
    public async Task MatchesAsync_ReturnsTrueForSameContent()
    {
        byte[] data = new byte[4096];
        Random.Shared.NextBytes(data);

        using MemoryStream stream = new(data);
        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream, CancellationToken.None);

        using MemoryStream stream2 = new(data);
        bool matches = await fingerprint.MatchesAsync(stream2, CancellationToken.None);

        Assert.True(matches);
    }

    [Fact]
    public async Task MatchesAsync_ReturnsFalseForDifferentContent()
    {
        byte[] data1 = new byte[4096];
        byte[] data2 = new byte[4096];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        using MemoryStream stream1 = new(data1);
        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream1, CancellationToken.None);

        using MemoryStream stream2 = new(data2);
        bool matches = await fingerprint.MatchesAsync(stream2, CancellationToken.None);

        Assert.False(matches);
    }

    [Fact]
    public async Task MatchesAsync_ReturnsFalseForDifferentSize()
    {
        byte[] data = new byte[1024];
        using MemoryStream stream = new(data);
        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream, CancellationToken.None);

        byte[] longerData = new byte[2048];
        using MemoryStream stream2 = new(longerData);
        bool matches = await fingerprint.MatchesAsync(stream2, CancellationToken.None);

        Assert.False(matches);
    }

    [Fact]
    public void Equals_ReturnsTrueForSameComponents()
    {
        byte[] hash = new byte[32];
        Random.Shared.NextBytes(hash);

        SourceFingerprint a = new(100, hash);
        SourceFingerprint b = new(100, (byte[])hash.Clone());

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentSize()
    {
        byte[] hash = new byte[32];
        SourceFingerprint a = new(100, hash);
        SourceFingerprint b = new(200, hash);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_ReturnsFalseForNull()
    {
        SourceFingerprint fingerprint = new(100, new byte[32]);

        Assert.False(fingerprint.Equals(null));
    }

    [Fact]
    public async Task ComputeAsync_EmptyStream_ProducesValidFingerprint()
    {
        using MemoryStream stream = new([]);

        SourceFingerprint fingerprint = await SourceFingerprint.ComputeAsync(stream, CancellationToken.None);

        Assert.Equal(0, fingerprint.FileSize);
        Assert.Equal(32, fingerprint.StripedHash.Length);
    }
}
