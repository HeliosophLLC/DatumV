using Heliosoph.DatumV.Indexing;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// PR13a-1 tests for the IDXT tail-flip-as-commit protocol on
/// <c>.datum-index</c>. The trailing 8 bytes (4B SectionCount echo +
/// 4B "IDXT" magic) are the atomic-commit signal — a writer that
/// crashed mid-commit leaves the file without IDXT and the reader
/// must reject it as torn so callers fall back to "no valid index"
/// (PR9.5 invalidate-on-stale path).
/// </summary>
public sealed class UnifiedIndexTailFlipTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr13a_tail_{Guid.NewGuid():N}");

    public UnifiedIndexTailFlipTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Roundtrip_Open_AcceptsTailedFile()
    {
        // The minimal SourceIndexSet still rounds through writer + reader.
        // This is the "happy path" — the tail is written; reader reads back.
        string path = WriteEmptyIndex();

        using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(path);
        Assert.NotNull(mapped.IndexSet);
    }

    [Fact]
    public void Open_TailMagicMissing_RejectsAsTorn()
    {
        // Simulate a writer that crashed before flushing the trailing
        // IDXT bytes — overwrite the magic with zeroes. Reader must
        // surface InvalidDataException so callers can treat the index
        // as missing and fall through to scan + REINDEX.
        string path = WriteEmptyIndex();

        long fileLength = new FileInfo(path).Length;
        using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Zero out the last 4 bytes (the IDXT magic).
            fs.Position = fileLength - 4;
            fs.Write(new byte[] { 0, 0, 0, 0 });
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => UnifiedIndexReader.Open(path));
        Assert.Contains("IDXT", ex.Message);
    }

    [Fact]
    public void Open_TailSectionCountMismatch_RejectsAsTorn()
    {
        // The 4-byte SectionCount echo at the start of the tail is a
        // cheap consistency check — if a writer wrote the IDXT magic
        // but the section count doesn't match what's in the header,
        // something is structurally wrong.
        string path = WriteEmptyIndex();

        long fileLength = new FileInfo(path).Length;
        using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Corrupt the section-count echo in the tail (first 4 bytes
            // of the 8-byte tail block).
            fs.Position = fileLength - 8;
            fs.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }); // bogus large value
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => UnifiedIndexReader.Open(path));
        Assert.Contains("SectionCount", ex.Message);
    }

    [Fact]
    public void Open_FileTruncatedBelowTail_RejectsAsTorn()
    {
        // A writer that died after only emitting the header writes a
        // file shorter than HeaderSize + TailSize. The reader's "is
        // this file even big enough" check must fire BEFORE we walk
        // the directory.
        string path = WriteEmptyIndex();

        // Truncate to just the header.
        using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(UnifiedIndexWriter.HeaderSize);
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => UnifiedIndexReader.Open(path));
        Assert.Contains("torn write", ex.Message);
    }

    [Fact]
    public void Open_HeaderFileLengthMismatch_RejectsAsTorn()
    {
        // The header carries FileLength; the on-disk file is the
        // ground truth. If the writer crashed after backpatching the
        // header but before writing the tail, the on-disk size will
        // be less than the header claims.
        string path = WriteEmptyIndex();

        // Append junk bytes so on-disk size exceeds header.FileLength.
        using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Position = fs.Length;
            fs.Write(new byte[16]);
        }

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => UnifiedIndexReader.Open(path));
        Assert.Contains("disagrees", ex.Message);
    }

    [Fact]
    public void WrittenFile_EndsWithIdxtMagic()
    {
        // Spot-check the on-disk shape: last 4 bytes are "IDXT".
        string path = WriteEmptyIndex();

        long fileLength = new FileInfo(path).Length;
        Assert.True(fileLength > UnifiedIndexWriter.TailSize);

        byte[] tail = File.ReadAllBytes(path)[(int)(fileLength - 4)..];
        Assert.Equal((byte)'I', tail[0]);
        Assert.Equal((byte)'D', tail[1]);
        Assert.Equal((byte)'X', tail[2]);
        Assert.Equal((byte)'T', tail[3]);
    }

    // ────────────────────────────── helpers ──────────────────────────────

    private string WriteEmptyIndex()
    {
        string path = Path.Combine(_tempDir, $"empty_{Guid.NewGuid():N}.datum-index");

        SourceFingerprint fingerprint = new(fileSize: 0, stripedHash: new byte[32]);
        SourceIndexSet indexSet = new(fingerprint, new Dictionary<string, SourceIndex>());

        using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        UnifiedIndexWriter.Write(indexSet, fs);
        return path;
    }
}
