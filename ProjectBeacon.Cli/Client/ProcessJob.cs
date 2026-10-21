using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProjectBeacon.Cli.Client;

/// <summary>
/// Assigns every process the client starts to a Windows job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so a closed
/// console window, a crash, or any other hard exit kills the serve and llama-swap trees at the kernel level.
/// Without this an orphaned serve keeps holding the client port and breaks the next client run with 401s.
/// </summary>
internal static class ProcessJob
{
    private static readonly object _gate = new();
    private static IntPtr _handle = IntPtr.Zero;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    internal static bool IsActive => _handle != IntPtr.Zero;

    /// <summary>
    /// Best effort: false on non-Windows, job creation failure, or a process that cannot be assigned
    /// (for example because it already created children).
    /// </summary>
    public static bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                _handle = CreateJobObject(IntPtr.Zero, null);
                if (_handle == IntPtr.Zero)
                    return false;
                var extended = new JobExtendedLimitInformation
                {
                    BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
                };
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref extended, Marshal.SizeOf<JobExtendedLimitInformation>()))
                {
                    _ = CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }
            }
            try
            {
                return AssignProcessToJobObject(_handle, process.Handle);
            }
            catch
            {
                return false;
            }
        }
    }

    // The extended structure must be exactly 144 bytes on current Windows (probed on the running OS):
    // basic(64) + IO_COUNTERS(32) + 4 memory fields(32) + 16 vendor-added trailing bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public ulong MinimumWorkingSetSize;
        public ulong MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public ulong Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public uint IoReadTransferCount;
        public uint IoWriteTransferCount;
        public uint IoReadOperationCount;
        public uint IoWriteOperationCount;
        public uint IoOtherOperationCount;
        public uint IoReadTransferBytes;
        public uint IoWriteTransferBytes;
        public uint IoOtherTransferBytes;
        public ulong ProcessMemoryLimit;
        public ulong JobMemoryLimit;
        public ulong PeakProcessMemoryUsed;
        public ulong PeakJobMemoryUsed;
        public ulong VendorReserved1;
        public ulong VendorReserved2;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr p1, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int informationClass, ref JobExtendedLimitInformation information, int informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}