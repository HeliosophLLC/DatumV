namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Cross-platform writer-coordination lock for v2 .datum files. Acquired by
/// <see cref="AcquireFor(string)"/>; released by <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the previous <c>FileShare.Read</c>-on-the-data-file convention,
/// which only excluded concurrent writers on Windows (mandatory file-share
/// enforcement). Linux treats <c>FileShare</c> as advisory — two writers
/// could open the same file with <c>FileShare.Read</c> and both succeed,
/// corrupting each other's output. This lock works on both platforms because
/// it relies on <see cref="FileMode.CreateNew"/>'s atomic create-or-fail
/// semantic, which the OS enforces regardless of file-share semantics.
/// </para>
/// <para><b>Scope of protection.</b> This lock coordinates writers that go
/// through <see cref="DatumFileWriterV2"/>. It is a <i>cooperative</i>
/// convention, not an OS-enforced mandatory lock on the data file itself.
/// Concretely:
/// <list type="bullet">
///   <item>Two <see cref="DatumFileWriterV2"/> openers on the same path
///   are correctly serialised on both Windows and Linux — second acquire
///   throws <see cref="IOException"/>.</item>
///   <item>On <b>Windows</b>, an external process opening the data file
///   directly with <c>FileAccess.Write</c> is additionally blocked by the
///   data stream's <c>FileShare.Read</c>, because Windows file-share
///   enforcement is mandatory.</item>
///   <item>On <b>Linux</b>, the kernel treats <c>FileShare</c> as
///   advisory. An external process that bypasses this writer class and
///   opens the data file directly is <i>not</i> blocked. The DatumV
///   library assumes exclusive ownership of its files, the same model
///   SQLite / Postgres / RocksDB rely on.</item>
/// </list>
/// If a future requirement demands hard exclusion against arbitrary
/// external writers on Linux, the upgrade path is <c>fcntl(F_SETLK)</c> on
/// the data file via P/Invoke — advisory in name but enforced against any
/// other process that also uses fcntl locking (most database tooling does).
/// </para>
/// <para>
/// Stale lock files (left behind by a writer crash) block subsequent writers
/// until manually removed. The exception message tells the user where the
/// lock file is so they can delete it after confirming no writer is running.
/// A future enhancement could embed the writer's PID and check liveness
/// before refusing acquisition; the current design favours correctness
/// over convenience.
/// </para>
/// </remarks>
internal sealed class WriterLockFile : IDisposable
{
    private const string LockSuffix = ".writer-lock";

    private readonly string _lockPath;
    private FileStream? _stream;

    private WriterLockFile(string lockPath, FileStream stream)
    {
        _lockPath = lockPath;
        _stream = stream;
    }

    /// <summary>
    /// Acquires the writer lock for <paramref name="datumPath"/>. Throws
    /// <see cref="IOException"/> if another writer holds the lock or the
    /// lock file already exists (stale from a previous crash).
    /// </summary>
    public static WriterLockFile AcquireFor(string datumPath)
    {
        string lockPath = datumPath + LockSuffix;
        try
        {
            FileStream stream = new(
                lockPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return new WriterLockFile(lockPath, stream);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Could not acquire writer lock for '{datumPath}' (lock file '{lockPath}' already exists). "
                + "Another writer is active, or a previous writer crashed and left a stale lock file — "
                + "in the latter case, delete the lock file to recover.", ex);
        }
    }

    public void Dispose()
    {
        if (_stream is null) return;
        FileStream stream = _stream;
        _stream = null;

        // Close the handle before unlinking. On Windows, deleting a file
        // with an open handle is allowed only with FileShare.Delete, which
        // we don't grant — so the order is: close, then delete. On Linux
        // unlink works regardless, but the same ordering is fine and is
        // exception-safe (if Delete throws, the handle is already closed).
        stream.Dispose();
        try
        {
            File.Delete(_lockPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup. If the delete fails (e.g. file was
            // removed by an external process between close and delete),
            // the lock semantic is still correct — what matters is that
            // no two writers ever held it simultaneously.
        }
    }
}
