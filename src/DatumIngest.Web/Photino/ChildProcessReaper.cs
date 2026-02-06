using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DatumIngest.Web.Photino;

// Windows Job Object that auto-kills adopted child processes when our
// process exits. Without this, a force-stopped debugger (Shift+F5, crash,
// kill -9) skips the C# `finally` that calls ViteDevServer.Stop, leaving
// node bound to :5173 and blocking the next launch.
//
// Mechanism: CreateJobObject + SetInformationJobObject with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. AssignProcessToJobObject puts the
// adopted process (and its descendants, since job membership inherits)
// into the job. The job handle stays open for the process lifetime; when
// our process terminates, the OS closes the handle, which fires the
// kill-on-close behaviour for every member.
//
// Nested-job note: vsdbg may run us inside its own job. Nested jobs are
// supported on Windows 8+ (which is fine — net10 only ships on Win10+).
// If AssignProcessToJobObject still fails we log and continue: the worst
// case is the legacy orphan-Vite behaviour we already had.
[SupportedOSPlatform("windows")]
internal static class ChildProcessReaper
{
    private static IntPtr _job;
    private static readonly object _lock = new();

    private static void EnsureJob()
    {
        if (_job != IntPtr.Zero) return;
        lock (_lock)
        {
            if (_job != IntPtr.Zero) return;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()})");
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buf, fDeleteOld: false);
                if (!SetInformationJobObject(
                        job,
                        JobObjectInfoType.ExtendedLimitInformation,
                        buf,
                        (uint)size))
                {
                    var err = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new InvalidOperationException(
                        $"SetInformationJobObject failed (Win32 error {err})");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            _job = job;
        }
    }

    public static void Adopt(Process child)
    {
        EnsureJob();
        if (!AssignProcessToJobObject(_job, child.Handle))
        {
            // Don't throw: in the worst case the user gets the same orphan
            // behaviour they would have without this class. Logging is enough.
            Console.Error.WriteLine(
                $"[Reaper] AssignProcessToJobObject failed for PID {child.Id} " +
                $"(Win32 error {Marshal.GetLastWin32Error()}); Vite may orphan on force-stop.");
        }
    }

    // Atomic kernel-level kill of every adopted process and its descendants.
    // Triggered by KILL_ON_JOB_CLOSE when the last handle to the job closes.
    // Used for clean shutdown — avoids Process.Kill(entireProcessTree: true)'s
    // race-against-dying-children exception flood. On force-stop the OS does
    // the same thing automatically when our process handle is reaped.
    public static void KillAll()
    {
        IntPtr handle = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum JobObjectInfoType : int
    {
        ExtendedLimitInformation = 9,
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
