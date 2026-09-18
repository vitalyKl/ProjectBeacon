namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Infrastructure.LlamaSwap;

public static class HostLoadSampler
{
    private static ulong _prevIdle;
    private static ulong _prevKernel;
    private static ulong _prevUser;
    private static bool _hasPrev;

    public static HostLoadDto Sample()
    {
        SampleMemory(out var used, out var total);
        return new HostLoadDto(SampleCpu(), used, total, SampleGpu(), DateTimeOffset.UtcNow);
    }

    public static object ToWire(HostLoadDto load) => new
    {
        cpuPercent = load.CpuPercent,
        ramUsedBytes = load.RamUsedBytes,
        ramTotalBytes = load.RamTotalBytes,
        gpu = load.Gpu is null ? null : new
        {
            name = load.Gpu.Name,
            utilizationPercent = load.Gpu.UtilizationPercent,
            memoryUsedBytes = load.Gpu.MemoryUsedBytes,
            memoryTotalBytes = load.Gpu.MemoryTotalBytes
        },
        sampledAt = load.SampledAt
    };

    public static GpuLoadDto? ParseNvidiaSmi(string stdout)
    {
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var parts = line.Split(',');
        if (parts.Length < 4)
            return null;
        var name = parts[0].Trim();
        if (name.Length == 0)
            return null;
        double? util = TryNumber(parts[1]);
        var usedMib = TryNumber(parts[2]);
        var totalMib = TryNumber(parts[3]);
        return new GpuLoadDto(
            name,
            util,
            usedMib is { } u ? (long)(u * 1024 * 1024) : null,
            totalMib is { } t ? (long)(t * 1024 * 1024) : null);
    }

    internal static string? FormatMemory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return raw.Trim();
        const double mb = 1024d * 1024d;
        const double gb = mb * 1024d;
        if (value >= gb)
            return (value / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (value >= mb)
            return (value / mb).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        if (value >= 1024d)
            return (value / 1024d).ToString("0.00", CultureInfo.InvariantCulture) + " KB";
        return value.ToString("0", CultureInfo.InvariantCulture) + " B";
    }

    private static double? SampleCpu()
    {
        if (OperatingSystem.IsWindows())
            return SampleCpuWindows();
        return SampleCpuProc();
    }

    private static double? SampleCpuWindows()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        var idleNow = ToUInt64(idle);
        var kernelNow = ToUInt64(kernel);
        var userNow = ToUInt64(user);
        if (!_hasPrev)
        {
            _prevIdle = idleNow;
            _prevKernel = kernelNow;
            _prevUser = userNow;
            _hasPrev = true;
            return null;
        }
        var idleDelta = idleNow - _prevIdle;
        var totalDelta = kernelNow - _prevKernel + (userNow - _prevUser);
        _prevIdle = idleNow;
        _prevKernel = kernelNow;
        _prevUser = userNow;
        if (totalDelta == 0)
            return null;
        var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        return Math.Clamp(100d * busy / totalDelta, 0, 100);
    }

    private static double? SampleCpuProc()
    {
        try
        {
            if (!File.Exists("/proc/stat"))
                return null;
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
                return null;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                return null;
            var user = ulong.Parse(parts[1], CultureInfo.InvariantCulture);
            var nice = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            var system = ulong.Parse(parts[3], CultureInfo.InvariantCulture);
            var idle = ulong.Parse(parts[4], CultureInfo.InvariantCulture);
            var total = user + nice + system + idle;
            if (!_hasPrev)
            {
                _prevIdle = idle;
                _prevKernel = total;
                _hasPrev = true;
                return null;
            }
            var idleDelta = idle - _prevIdle;
            var totalDelta = total - _prevKernel;
            _prevIdle = idle;
            _prevKernel = total;
            if (totalDelta == 0)
                return null;
            var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
            return Math.Clamp(100d * busy / totalDelta, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private static void SampleMemory(out long? used, out long? total)
    {
        used = null;
        total = null;
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
                return;
            total = (long)status.TotalPhys;
            used = (long)(status.TotalPhys - status.AvailPhys);
            return;
        }
        try
        {
            if (!File.Exists("/proc/meminfo"))
                return;
            long? memTotal = null;
            long? memAvail = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    memTotal = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    memAvail = ParseKb(line);
                if (memTotal is not null && memAvail is not null)
                    break;
            }
            if (memTotal is not { } t || t <= 0)
                return;
            total = t;
            used = t - (memAvail ?? 0);
        }
        catch
        {
        }
    }

    private static GpuLoadDto? SampleGpu()
    {
        var bin = WorkstationActions.Which("nvidia-smi") ?? WorkstationActions.Which("nvidia-smi.exe");
        if (bin is null)
            return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bin,
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return ParseNvidiaSmi(output);
        }
        catch
        {
            return null;
        }
    }

    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            return null;
        return kb * 1024;
    }

    private static double? TryNumber(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
