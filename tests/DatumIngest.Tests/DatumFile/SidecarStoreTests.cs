using System.Buffers.Binary;
using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Tests.DatumFile;

/// <summary>
/// Round-trip and tamper-detection tests for <see cref="SidecarWriteStore"/> and
/// <see cref="SidecarReadStore"/>, with an emphasis on the Phase 9b payload-hash check.
/// </summary>
public sealed class SidecarStoreTests : ServiceTestBase
{
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for sidecar test files.</summary>
    public SidecarStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(), "SidecarStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>Removes the temporary directory.</summary>
    public override void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        base.Dispose();
    }

    [Fact]
    public void OpenForAppend_WhileReaderHoldsMmap_Succeeds()
    {
        // Regression: SidecarReadStore previously held the mmap'd file with
        // FileShare.Read (the default share mode for FileMode.Open). On
        // Windows that blocks SidecarWriteStore.OpenForAppend's FileAccess.
        // ReadWrite open with "the process cannot access the file" — which
        // surfaces as the second back-to-back INSERT into a string column
        // failing during chat persistence.
        //
        // The fix opens the underlying FileStream with FileShare.ReadWrite |
        // FileShare.Delete so a concurrent appender can extend the file.
        // Append-only growth means the reader's mmap'd view of the
        // pre-append region remains valid; it just doesn't see the new
        // bytes (and doesn't need to).
        string path = Path.Combine(_tempDirectory, "concurrent_append.datum-blob");
        ulong fingerprint;

        using (SidecarWriteStore writer = new(path))
        {
            writer.Append("first payload"u8);
            fingerprint = writer.Fingerprint;
        }

        using SidecarReadStore reader = new(path, fingerprint);

        // Reader is alive; appender must still be able to open.
        using (SidecarWriteStore appender = SidecarWriteStore.OpenForAppend(path))
        {
            appender.Append("second payload"u8);
        }

        // Existing reader's view of the pre-append region still works.
        Assert.Equal(
            "first payload"u8.ToArray(),
            reader.Read(SidecarConstants.HeaderSize, "first payload"u8.Length).ToArray());
    }

    [Fact]
    public void RoundTrip_PayloadHashVerifies()
    {
        string path = Path.Combine(_tempDirectory, "round_trip.datum-blob");
        ulong fingerprint;

        using (SidecarWriteStore writer = new(path))
        {
            writer.Append("hello"u8);
            writer.Append("world"u8);
            fingerprint = writer.Fingerprint;
        }

        using SidecarReadStore reader = new(path, fingerprint);
        Assert.Equal("hello"u8.ToArray(), reader.Read(SidecarConstants.HeaderSize, 5).ToArray());
        Assert.Equal("world"u8.ToArray(), reader.Read(SidecarConstants.HeaderSize + 5, 5).ToArray());
    }

    [Fact]
    public void Open_TamperedPayload_Throws()
    {
        string path = Path.Combine(_tempDirectory, "tampered.datum-blob");
        ulong fingerprint;

        using (SidecarWriteStore writer = new(path))
        {
            writer.Append("the quick brown fox"u8);
            fingerprint = writer.Fingerprint;
        }

        // Flip one byte in the payload (well past the header).
        byte[] bytes = File.ReadAllBytes(path);
        bytes[SidecarConstants.HeaderSize + 4] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => new SidecarReadStore(path, fingerprint));
        Assert.Contains("payload corrupted", ex.Message);
    }

    [Fact]
    public void Open_ZeroHashLegacyFile_SkipsVerification()
    {
        // Simulate a sidecar produced by a writer predating Phase 9b: the hash slot
        // is zero. The reader must accept it and skip the check rather than fail.
        string path = Path.Combine(_tempDirectory, "legacy.datum-blob");
        ulong fingerprint;

        using (SidecarWriteStore writer = new(path))
        {
            writer.Append("legacy payload"u8);
            fingerprint = writer.Fingerprint;
        }

        // Zero out the hash bytes the writer just patched in.
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(SidecarConstants.PayloadHashOffset, SeekOrigin.Begin);
            Span<byte> zero = stackalloc byte[8];
            fs.Write(zero);
        }

        // Should open without throwing.
        using SidecarReadStore reader = new(path, fingerprint);
        Assert.Equal("legacy payload"u8.ToArray(),
            reader.Read(SidecarConstants.HeaderSize, "legacy payload"u8.Length).ToArray());
    }

    [Fact]
    public void Open_FingerprintMismatch_ThrowsBeforeHashCheck()
    {
        string path = Path.Combine(_tempDirectory, "fingerprint_mismatch.datum-blob");

        using (SidecarWriteStore writer = new(path))
        {
            writer.Append("payload"u8);
        }

        ulong wrongFingerprint = 0xDEADBEEFCAFEBABEUL;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => new SidecarReadStore(path, wrongFingerprint));
        Assert.Contains("fingerprint", ex.Message);
    }

    [Fact]
    public void Dispose_NoAppend_LeavesNoFile()
    {
        string path = Path.Combine(_tempDirectory, "never_written.datum-blob");

        using (SidecarWriteStore writer = new(path))
        {
            // No Append calls.
            Assert.False(writer.WasMaterialized);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HeaderSize_IsConsumedByOffset()
    {
        // Sanity check: the first Append's offset must equal HeaderSize, since the
        // header is the only thing written before payload bytes.
        string path = Path.Combine(_tempDirectory, "first_offset.datum-blob");

        using SidecarWriteStore writer = new(path);
        (long offset, long length) = writer.Append("first"u8);
        Assert.Equal(SidecarConstants.HeaderSize, offset);
        Assert.Equal(5, length);
    }
}
