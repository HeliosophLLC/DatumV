using System.Runtime.InteropServices;

namespace DatumIngest.Model;

/// <summary>
/// Cross-platform reserve / commit / release of process virtual memory.
/// Lets <see cref="Arena"/> reserve a large virtual-address range up front
/// and commit pages on demand, so the base pointer stays stable for the
/// arena's lifetime even as capacity grows.
/// </summary>
/// <remarks>
/// <para>
/// Pointer stability is the load-bearing property: parallel scalar dispatch
/// holds <see cref="Span{T}"/> values across calls that may trigger arena
/// growth on another thread. With the prior mmap-and-remap design those
/// spans dangled the instant another worker's write reallocated the
/// mapping. Reserve-once-commit-on-demand makes that race impossible
/// because the address range never moves.
/// </para>
/// <para>
/// The reservation itself is cheap — it consumes virtual address space
/// (64-bit Windows has ~128 TB user-mode, Linux/macOS comparable) but no
/// physical memory and no commit charge. Only <see cref="Commit"/> consumes
/// real resources.
/// </para>
/// <para>
/// Syscalls per platform:
/// <list type="bullet">
///   <item>Windows: <c>VirtualAlloc(MEM_RESERVE, PAGE_NOACCESS)</c> →
///         <c>VirtualAlloc(MEM_COMMIT, PAGE_READWRITE)</c> →
///         <c>VirtualFree(MEM_RELEASE)</c>.</item>
///   <item>Linux / macOS: <c>mmap(PROT_NONE, MAP_ANONYMOUS|MAP_PRIVATE)</c> →
///         <c>mprotect(PROT_READ|PROT_WRITE)</c> → <c>munmap</c>.</item>
/// </list>
/// </para>
/// </remarks>
internal static class VirtualMemory
{
    /// <summary>OS page size in bytes (4 KB on most platforms, 16 KB on Apple Silicon).</summary>
    public static int PageSize { get; } = Environment.SystemPageSize;

    /// <summary>
    /// Reserves <paramref name="size"/> bytes of virtual address space without
    /// committing physical memory. The returned pointer is stable for the
    /// reservation's lifetime; callers must <see cref="Commit"/> sub-ranges
    /// before touching them.
    /// </summary>
    /// <param name="size">Reservation size in bytes. Rounded up to page granularity by the OS.</param>
    public static unsafe byte* Reserve(long size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        return OperatingSystem.IsWindows() ? WindowsReserve(size) : PosixReserve(size);
    }

    /// <summary>
    /// Commits <paramref name="size"/> bytes starting at
    /// <paramref name="basePointer"/> + <paramref name="offset"/>, transitioning
    /// those pages from reserved-only to backed by physical memory on first
    /// touch.
    /// </summary>
    /// <param name="basePointer">Base pointer returned by <see cref="Reserve"/>.</param>
    /// <param name="offset">Byte offset within the reservation. Must be page-aligned for portability.</param>
    /// <param name="size">Number of bytes to commit. Rounded up to page granularity by the OS.</param>
    public static unsafe void Commit(byte* basePointer, long offset, long size)
    {
        if (size <= 0) return;
        if (OperatingSystem.IsWindows())
        {
            WindowsCommit(basePointer + offset, size);
        }
        else
        {
            PosixCommit(basePointer + offset, size);
        }
    }

    /// <summary>
    /// Releases the entire reservation at <paramref name="basePointer"/>,
    /// decommitting any committed sub-ranges in the process. <paramref name="size"/>
    /// is the reservation size passed to <see cref="Reserve"/> — required by
    /// <c>munmap</c>; ignored on Windows where <c>VirtualFree</c> recovers it
    /// from the reservation header.
    /// </summary>
    public static unsafe void Release(byte* basePointer, long size)
    {
        if (basePointer == null) return;
        if (OperatingSystem.IsWindows())
        {
            WindowsRelease(basePointer);
        }
        else
        {
            PosixRelease(basePointer, size);
        }
    }

    /// <summary>Rounds <paramref name="bytes"/> up to a multiple of <see cref="PageSize"/>.</summary>
    public static long RoundUpToPage(long bytes)
    {
        long pageMask = PageSize - 1;
        return (bytes + pageMask) & ~pageMask;
    }

    // ───────────────────────── Windows ─────────────────────────

    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32", SetLastError = true)]
    private static extern unsafe void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    private static unsafe byte* WindowsReserve(long size)
    {
        void* p = VirtualAlloc(null, (nuint)size, MEM_RESERVE, PAGE_NOACCESS);
        if (p == null)
        {
            int err = Marshal.GetLastWin32Error();
            throw new OutOfMemoryException(
                $"VirtualAlloc(MEM_RESERVE, {size:N0} bytes) failed (Win32 error {err}). " +
                $"Likely causes: process VA exhaustion on a 32-bit host, or a job-object commit cap.");
        }
        return (byte*)p;
    }

    private static unsafe void WindowsCommit(byte* address, long size)
    {
        void* p = VirtualAlloc(address, (nuint)size, MEM_COMMIT, PAGE_READWRITE);
        if (p == null)
        {
            int err = Marshal.GetLastWin32Error();
            throw new OutOfMemoryException(
                $"VirtualAlloc(MEM_COMMIT, {size:N0} bytes) at 0x{(nint)address:X} failed (Win32 error {err}). " +
                $"Likely cause: system commit charge exhausted (RAM + page file).");
        }
    }

    private static unsafe void WindowsRelease(byte* basePointer)
    {
        if (!VirtualFree(basePointer, 0, MEM_RELEASE))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"VirtualFree(MEM_RELEASE) at 0x{(nint)basePointer:X} failed (Win32 error {err}).");
        }
    }

    // ───────────────────────── POSIX (Linux + macOS) ─────────────────────────

    private const int PROT_NONE = 0x0;
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int MAP_PRIVATE = 0x02;

    // MAP_ANONYMOUS differs: 0x20 on Linux, 0x1000 on macOS (where it's named MAP_ANON).
    // Cached once at type init so the per-call hot path is a single field read.
    private static readonly int MAP_ANONYMOUS = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;

    // mmap returns (void*)-1 on failure (MAP_FAILED). IntPtr comparison handles both 64-bit and 32-bit.
    private static readonly IntPtr MAP_FAILED = new(-1);

    [DllImport("libc", SetLastError = true, EntryPoint = "mmap")]
    private static extern IntPtr LibcMmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true, EntryPoint = "mprotect")]
    private static extern int LibcMprotect(IntPtr addr, nuint length, int prot);

    [DllImport("libc", SetLastError = true, EntryPoint = "munmap")]
    private static extern int LibcMunmap(IntPtr addr, nuint length);

    private static unsafe byte* PosixReserve(long size)
    {
        IntPtr p = LibcMmap(IntPtr.Zero, (nuint)size, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new OutOfMemoryException(
                $"mmap(PROT_NONE, {size:N0} bytes) failed (errno {err}). " +
                $"Likely causes: strict overcommit (vm.overcommit_memory=2), RLIMIT_AS, or vm.max_map_count.");
        }
        return (byte*)p;
    }

    private static unsafe void PosixCommit(byte* address, long size)
    {
        int rc = LibcMprotect((IntPtr)address, (nuint)size, PROT_READ | PROT_WRITE);
        if (rc != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new OutOfMemoryException(
                $"mprotect(PROT_RW, {size:N0} bytes) at 0x{(nint)address:X} failed (errno {err}).");
        }
    }

    private static unsafe void PosixRelease(byte* basePointer, long size)
    {
        int rc = LibcMunmap((IntPtr)basePointer, (nuint)size);
        if (rc != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"munmap at 0x{(nint)basePointer:X} (size {size:N0}) failed (errno {err}).");
        }
    }
}
