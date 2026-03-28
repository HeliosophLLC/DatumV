using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Tests.DatumFile.Sidecar;

/// <summary>
/// Tests for <see cref="BlobSourceStream"/> — the seekable <see cref="Stream"/>
/// view over a window of an <see cref="IBlobSource"/>. The stream is the bridge
/// that lets FFmpeg's <c>IOContext.ReadStream</c> drive decoding directly from
/// a memory-mapped <c>.datum-blob</c> region without intermediate copies.
/// </summary>
public sealed class BlobSourceStreamTests
{
    private static byte[] BuildPayload(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i % 251); // distinctive pattern
        return bytes;
    }

    [Fact]
    public void ReportsLength_Position_AndCapabilities()
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 16, length: 32);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(32, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Read_ReturnsBytesFromBaseOffset_AdvancesPosition()
    {
        byte[] payload = BuildPayload(64);
        using BlobSourceStream stream = new(new InMemoryBlobSource(payload), baseOffset: 16, length: 32);

        byte[] buffer = new byte[10];
        int read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(10, read);
        Assert.Equal(10, stream.Position);
        // baseOffset=16 → first 10 bytes are payload[16..25]
        for (int i = 0; i < 10; i++) Assert.Equal(payload[16 + i], buffer[i]);
    }

    [Fact]
    public void Read_AtEndOfSlice_ReturnsZero()
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 0, length: 8);
        byte[] buffer = new byte[16];
        Assert.Equal(8, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Read_ClampsToSliceEnd_NotUnderlyingLength()
    {
        // 64-byte source, slice [16, 48). A 100-byte read from position 0
        // returns 32 bytes, not 84.
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 16, length: 32);
        byte[] buffer = new byte[100];
        Assert.Equal(32, stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Seek_Begin_Current_End_AllRespectSliceBounds()
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 0, length: 32);

        Assert.Equal(10, stream.Seek(10, SeekOrigin.Begin));
        Assert.Equal(10, stream.Position);

        Assert.Equal(15, stream.Seek(5, SeekOrigin.Current));
        Assert.Equal(15, stream.Position);

        Assert.Equal(32, stream.Seek(0, SeekOrigin.End));
        Assert.Equal(0, stream.Seek(-32, SeekOrigin.End));
    }

    [Theory]
    [InlineData(-1, SeekOrigin.Begin)]
    [InlineData(33, SeekOrigin.Begin)]
    [InlineData(1, SeekOrigin.End)]
    [InlineData(-33, SeekOrigin.End)]
    public void Seek_OutOfBounds_Throws(long offset, SeekOrigin origin)
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 0, length: 32);
        Assert.Throws<IOException>(() => stream.Seek(offset, origin));
    }

    [Fact]
    public void Position_SetterClampsToSliceBounds()
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(64)), baseOffset: 0, length: 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = 33);
    }

    [Fact]
    public void Write_Throws()
    {
        using BlobSourceStream stream = new(new InMemoryBlobSource(BuildPayload(8)), baseOffset: 0, length: 8);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(16));
    }

    [Fact]
    public void Dispose_BlocksFurtherAccess_LeavesSourceUntouched()
    {
        InMemoryBlobSource source = new(BuildPayload(64));
        BlobSourceStream stream = new(source, baseOffset: 0, length: 32);
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);

        // Underlying source must remain usable — disposing the slice view doesn't
        // claim ownership of the source.
        Assert.False(source.Disposed);
        ReadOnlySpan<byte> bytes = source.Read(0, 4);
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public void Constructor_NegativeOffsetOrLength_Throws()
    {
        InMemoryBlobSource source = new(BuildPayload(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobSourceStream(source, baseOffset: -1, length: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobSourceStream(source, baseOffset: 0, length: -1));
    }

    // ─────────────────────── Test double ───────────────────────

    /// <summary>
    /// Minimal in-memory <see cref="IBlobSource"/> for testing. Wraps a managed
    /// <see cref="byte"/>[] and serves spans directly from it. Tracks disposal so
    /// tests can verify ownership boundaries.
    /// </summary>
    private sealed class InMemoryBlobSource(byte[] payload) : IBlobSource
    {
        private readonly byte[] _payload = payload;
        public bool Disposed { get; private set; }

        public ReadOnlySpan<byte> Read(long offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > _payload.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), $"out of bounds: offset={offset}, length={length}, payload={_payload.Length}");
            }
            return _payload.AsSpan((int)offset, (int)length);
        }

        public void Dispose() => Disposed = true;
    }
}
