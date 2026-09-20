namespace ProjectBeacon.Domain.Entities.Devices;

using ProjectBeacon.Domain.Common;

public class DeviceHostSample : Entity
{
    public DeviceHostSample() { }

    public Guid DeviceId { get; private set; }
    public DateTime SampledAt { get; private set; }
    public double? CpuPercent { get; private set; }
    public long? RamUsedBytes { get; private set; }
    public long? RamTotalBytes { get; private set; }
    public string? GpuName { get; private set; }
    public double? GpuUtilizationPercent { get; private set; }
    public long? GpuMemoryUsedBytes { get; private set; }
    public long? GpuMemoryTotalBytes { get; private set; }

    public static DeviceHostSample Create(
        Guid deviceId,
        DateTime sampledAt,
        double? cpuPercent,
        long? ramUsedBytes,
        long? ramTotalBytes,
        string? gpuName,
        double? gpuUtilizationPercent,
        long? gpuMemoryUsedBytes,
        long? gpuMemoryTotalBytes)
    {
        var sample = Entity.New<DeviceHostSample>();
        sample.DeviceId = deviceId;
        sample.SampledAt = sampledAt;
        sample.CpuPercent = cpuPercent;
        sample.RamUsedBytes = ramUsedBytes;
        sample.RamTotalBytes = ramTotalBytes;
        sample.GpuName = gpuName;
        sample.GpuUtilizationPercent = gpuUtilizationPercent;
        sample.GpuMemoryUsedBytes = gpuMemoryUsedBytes;
        sample.GpuMemoryTotalBytes = gpuMemoryTotalBytes;
        return sample;
    }
}
