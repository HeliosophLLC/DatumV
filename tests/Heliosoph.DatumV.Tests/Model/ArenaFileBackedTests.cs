using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests for the file-backed <see cref="Arena"/> mode produced by <see cref="Arena.CreateFileBacked"/>.
/// File-backed arenas live on disk so the OS can page payload bytes out of working set under
/// memory pressure — the actual memory-relief feature that "spill" implies. They have file
/// identity, are not pooled, and are deleted on dispose.
/// </summary>
public sealed class ArenaFileBackedTests : ServiceTestBase
{
    /// <summary>
    /// Allocates a fresh GUID-prefixed temp directory for a test. The directory itself
    /// (rather than just a temp file path) so the test can assert that files inside have
    /// been cleaned up after dispose.
    /// </summary>
    private static string CreateTempDirectory()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            $"arena-fb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FileBacked_FlagSet()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            using Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            Assert.True(arena.IsFileBacked);
            Assert.False(arena.IsAllocated, "Arena allocates lazily; not yet allocated before first write.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_FirstAppend_CreatesFile()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            Assert.False(File.Exists(filePath));

            arena.AppendString("hello");

            Assert.True(File.Exists(filePath));
            Assert.True(arena.IsAllocated);

            arena.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_RoundtripString()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            using Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            const string payload = "the quick brown fox jumps over the lazy dog";
            (long offset, int length) = arena.AppendString(payload);

            string readBack = arena.GetString(offset, length);
            Assert.Equal(payload, readBack);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_GrowResizesFile_PreservesExistingBytes()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            // Initial capacity gets floored at Arena's DefaultCapacity (1 MB). To force grow,
            // write more than 1 MB of payload.
            using Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            const string sentinel1 = "FIRST-MARKER-BEFORE-GROW";
            (long off1, int len1) = arena.AppendString(sentinel1);

            // Force grow by writing >1 MB of data.
            string big = new string('x', 1_500_000);
            (long off2, int len2) = arena.AppendString(big);

            const string sentinel2 = "SECOND-MARKER-AFTER-GROW";
            (long off3, int len3) = arena.AppendString(sentinel2);

            // File should be large enough to hold all that.
            FileInfo fi = new(filePath);
            Assert.True(fi.Length >= big.Length, $"file length {fi.Length} should be >= {big.Length}");

            // Sentinels written before AND after the grow must both still resolve correctly.
            // This is the load-bearing claim — file-backed grow uses unmap/SetLength/remap and
            // must not corrupt or lose existing bytes.
            Assert.Equal(sentinel1, arena.GetString(off1, len1));
            Assert.Equal(big, arena.GetString(off2, len2));
            Assert.Equal(sentinel2, arena.GetString(off3, len3));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_Dispose_DeletesBackingFile()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);
            arena.AppendString("touch");
            Assert.True(File.Exists(filePath));

            arena.Dispose();

            Assert.False(File.Exists(filePath),
                "Dispose on a file-backed arena must delete the backing file.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_DisposeBeforeFirstWrite_NoFileToDelete()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);
            // No writes — file was never created.
            Assert.False(File.Exists(filePath));

            arena.Dispose(); // must not throw
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_Pool_Throws()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            using Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            // Reflective access — Pool() is internal, but tests in the same project see it via
            // [InternalsVisibleTo]. If that doesn't apply, fall back to verifying the public
            // property contract (IsFileBacked → can't be pooled).
            // Direct call: Arena.Pool() is internal; the test project sees it via the standard
            // InternalsVisibleTo attribute. If this build setup ever changes, swap for a
            // PoolBacking.TryReturn-based assertion (which exercises the same guard indirectly).
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => arena.Pool());
            Assert.Contains("file identity", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_Reset_Throws()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            using Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => arena.Reset());
            Assert.Contains("end of life", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_ReturnArena_DisposesInsteadOfPooling()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);
            arena.AppendString("touch");
            Assert.True(File.Exists(filePath));

            Pool pool = GetService<Pool>();
            // The shared refcount path treats file-backed and anonymous arenas uniformly on
            // the entry side; only the terminal action diverges. We need refcount == 1 to
            // hit the terminal branch with a single ReturnArena call — bump it ourselves
            // (mirrors how pool.RentArena would AddReference for an anonymous rent).
            arena.AddReference();

            bool released = pool.ReturnArena(arena);

            Assert.True(released, "Refcount hit zero; ReturnArena should report a full release.");
            Assert.False(File.Exists(filePath),
                "File-backed arenas dispose (and delete the file) on terminal release rather than pooling.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileBacked_ConstructorRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => Arena.CreateFileBacked("", initialCapacity: 1024));
    }

    [Fact]
    public void FileBacked_ConstructorRejectsNullPath()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null
        // (derives from ArgumentException). Assert.Throws<T> wants the exact runtime type.
        Assert.Throws<ArgumentNullException>(() => Arena.CreateFileBacked(null!, initialCapacity: 1024));
    }

    [Fact]
    public void FileBacked_ConstructorThrowsIfFileAlreadyExists()
    {
        string dir = CreateTempDirectory();
        try
        {
            string filePath = Path.Combine(dir, "data.arena");
            File.WriteAllText(filePath, "stale content from a prior crashed process");

            Arena arena = Arena.CreateFileBacked(filePath, initialCapacity: 1024 * 1024);

            // Allocation is deferred to first write, where FileMode.CreateNew sees the
            // existing file and throws. This is the design contract — surface the orphan
            // explicitly rather than silently reusing it.
            Assert.Throws<IOException>(() => arena.AppendString("touch"));

            arena.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
